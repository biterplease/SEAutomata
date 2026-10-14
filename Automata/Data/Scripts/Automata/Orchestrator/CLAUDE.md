# Orchestrator Architecture
The `Orchestrator` is the central brain for task distribution.

## Operation
- **Announcements only**: the **Orchestrator** embeds nothing and scans nothing. Construction Computers, Logistics Computers and Mining Surveyors are blocks of their own that announce jobs; the Orchestrator turns each new announcement into an auction, re-broadcasts it while open, picks the winners after the bid window and announces their task lists. Jobs without workable bids are dropped after 2 minutes (the announcer's next announcement starts them over). Implemented: Weld, Grind, DeliverMissingInventory, CollectInventorySurplus, MineOre.
- **Never embedded**: it is never embedded into anything; only the Drone Controller embeds a Construction Computer (its stand-alone work).

## Core concepts
- **Job**: Is something that needs be completed, from the perspective of the player
- **Task**: Is one or more intermediate steps needed to complete a job. Tasks are distributed in sets, that are a sequence of steps needed in order to complete the job.
- Refer to `OrchestratorHelpers.cs` for the **Job** and **Task** types; the logic is in `OrchestratorBlock.cs` (block and logic in one class).


## Full flow Examples:

### Building a block
- **Orchestrator**: check needed materials to build the block
- **Orchestrator**: announce the task, with a material list, total cargo capacity, and location
- **Logistics Computers**: if they have the total or partial inventory, respond to the task announcement with a bid that lets the Orchestrator know their fulfillment capacity
- **Drones**: respond with a bid containing their location, cargo capacity, capabilities, and current task
- **Orchestrator**: Analyze bids and assign tasks sets intelligently to individual drones
    - A task set will likely consist of various tasks:
        - Go to location, collect cargo, go to weld target location, weld the block, return home
    - The orchestrator should check bids, to optimize drone usage
        - If a single drone can carry all the needed inventory, and has the correct capabilities, and a single Logistics Computer reports having all the needed inventory, then it should assign that one drone, all tasks related to the job
        - If no single drone can handle all job-related tasks, it should start breaking down the job into smaller tasks that each of the bidding drones can handle, based on their capabilities and their proximity to the inventories


### Mining at location

Any drone could be equipped with an Ore Detector, and, if the player has the setting on the **Drone Controller**, it should report ore findings to the **Mining Surveyor**. The **Mining Surveyor** should cache this location and ore data, and report to the player. If the **Mining Surveyor** has the `ScanAndPublish` `WorkMode` enabled, it will then announce a Mining task, which an **Orchestrator** can respond to.

Ores should only be deposited in grids that have the correct capabilities, as reported by **Logistics Computers** on them: Cargo Space is the only capability needed for being a recipient. However, ore should only go to grids that:
- Have 1 or more refineries, that can handle the ore
- OR, have an inventory quota for the ore

Workflow example:
- **Mining Surveyor**: has a cached location of an ore, and has the correct work mode. Has a cached location of either a Logistics Computer with an ore quota, or a grid with a Refinery
- **Orchestrator**: Announce a mining task. Should include mining location and deposit location
- **Drones**: If it has a drill and the correct set of capabilities, respond to bid
- **Orchestrator**: Select a bid winner based on **Drone** capabilities.
- Winning **Drone**: Go to location viccinity, and mine the desired capacity.

## Logistics trips (2026-10-02)
`AssignLogistics`: the other ends (logistics bids) ranked by coverage, the drones by score; trips (dock, load, dock, unload) handed out round robin, each as much as the drone carries of what that end offers, up to `MAX_LOGISTICS_TRIPS` (12). A single supplier + drone covering everything is one trip; otherwise the job is chunked over several trips / drones / suppliers. Each drone's list ends with ReturnHome. What no bid covers is left (the requester announces it again). A drone that loads less than planned on a logistics trip carries on with what it got.
- Jobs and bids from other owners / factions (SenderOwnerId) are ignored. Winner announcements go to drones, logistics computers and construction computers (the announcer stops repeating an awarded job).

## Queue and failed announcements (2026-10-02)
Job announcements are queued (de-duplicated per announcer + job id) and auctioned up to `MaxConcurrentAuctions` (server config [Orchestrator], 10) at once, oldest first. An auction with no bid at all after `NoBidTimeoutSeconds` (60) is dropped and a `JobAnnouncementFailed` (channel JOB_ANNOUNCEMENT_FAILED, to construction / logistics computers / mining surveyors; `AnnouncerEntityId`, `JobId`, `HoldSeconds` = `FailedJobHoldSeconds`, 300) is sent until delivered (30 s); the job key is forgotten so the announcer's announcement after the hold starts a new auction. Auctions with bids that never add up are dropped after 2 min with a failure (60 s hold); refused jobs get one too. Failed / dropped jobs are remembered 30 s only (the re-announcement is taken). Winner announcements carry `AnnouncerEntityId`.

## Since then (2026-10-02)
- Orchestrators listen to each other's auctions (they carry `AnnouncerEntityId`): the lower entity id keeps a job, the other drops it quietly.
- Drone outcome reports (`JobStatusReport`) are counted; an aborted job is taken again soon. Bids whose `EntityId` isn't the sender are ignored.
- Every job is a dual award (drone + LC, two LCs for logistics): grind and MineOre without an LC bid are not awarded. MineOre: the drone needs a drill and an ore detector; the LC needs a refinery for the ore (or "accept ore from mining without a refinery") and the ore / ingot below Max. Won LCs keep their reservation until the drone reports.
- Behavior profiles: a drone whose profile the job type prefers ranks above all others (cargo drones for logistics). Full protocol: `TODO/10-handshake.md`.

## Designator jobs (2026-10-05)
- Jobs a player orders with a target designator are named `Designator...`: `DesignatorScoutPoint`, `DesignatorScoutArea` (industrial: ore scouts only), `DesignatorEngage`, `DesignatorEscort` (hostile: combat drones with the three AI blocks only, `HasDefenseSystems`). Single award (drone only), the player's tasks + ReturnHome, confirmed within `ConfirmTimeoutSeconds` or cancelled (`OrchestratorBlock.PlayerJobs.cs`). Combat orders show no GPS marker (the target moves).
- "Use broadcast controller" (terminal, next to the LCD tag; every Automata block has it): an accepted designator order goes out as a chat line through a working Broadcast Controller of the owner / faction on the orchestrator's construct (`Util/BroadcastControllerSender`, the mod API's `IMyChatBroadcastControllerComponent.SendMessage(string)`); its Broadcast target decides who reads it. Nothing else is sent yet.

## Audit fixes (2026-10-06)
- Dedupe: int.MaxValue entries (queued / auctioned) are checked every pass and forgotten when neither is running (they used to ignore a job forever after dormancy / a failed start / a load). Ignored repeats are logged once per job.
- No-bid timeout counts drone bids only (logistics computers' offers alone can't do a job).
- Restored winner announcements reach mining surveyors too.

## Multi-drone awards and withdrawals (2026-10-06)

- Awards are not 1 drone : 1 LC. Weld / logistics already split over several drones (round-robin shares / trips); grind
  and mine are pooled too (`AssignGrind` / `AssignMine`, up to `MAX_POOLED_DRONES` 10). A job bigger than one trip of
  every drone that bid waits up to `AUCTION_POOL_TICKS` (15 s) for more bids (`BidsCoverOneTrip`), then is split over
  all usable bids, even a single block. Mining amount is an estimate: several miners, sites offset by `MINER_SPACING`.
  Sequential weld jobs (2026-10-13, `JobAnnouncement` / `Auction.Sequential`: a construction computer's projection line, each
  block attached to the one before): no pooling wait, and the best-ranked drone with a useful trip share gets every trip
  (up to 64), one after another - other drones would only wait on its blocks and crowd it.
- Bid window: server config `BidCollectionWindowTicks` (default 300 = 5 s; was the hard-coded `AUCTION_WINDOW_TICKS`).
- `JOB_WITHDRAWN` (JobStatusReport: announcer, job, reason): an announcer takes a job back (mining order removed). The
  queue entry / running auction is dropped, the key forgotten, and a `JobAnnouncementFailed` with announcer 0 and the
  auction id goes out so the logistics computers that bid release their promise. Same failure (announcer 0) when an
  auction is left to a peer orchestrator.
- Designator tagging: player jobs may carry `RequiredDrone`; the auction then has `Auction.OnlyDroneEntityId` (other
  drones don't bid, other drone bids are refused). `BroadcastTagged` sends "tagged <name> (<mode>)" through the
  broadcast controller when that setting is on. See TODO/11.
- Parking protocol (2026-10-07; `StagingArea` = "Use parking protocol"): mode combobox Park on grid / Parking area / Voxel (`ParkingMode`, ProtoMember 10; only for drones managed by an orchestrator). Area size / offset in blocks (sliders show metres too), 50 m at most each way, offset 250 m; older saves in metres are converted once (`AreaInBlocks`). Area sliders and its draw only in area mode. Drone fallback: area > grid > any friendly grid > ground.

## Nicknames, LCD headers, award contents (2026-10-08)
- Every Automata block has a "Nickname" (terminal, under the game's Name: `NicknameControl` moves it there through `CustomControlGetter`); logs name blocks with `Nicknames.Of(entityId)` (nickname, else the terminal name, 16 characters at most, "..." when cut). Server side: entities are looked up by id.
- PB computers (orchestrator, LC, MC, CC) feed their LCDs with `BlockLcd` (the "Automata Log" LCD app draws the sprites on each client, see "LCD app" below; `LcdStyle` in their settings: colours, font size, header on / off, header page; same defaults as the drone) and log to a `BlockLog` (entries of several lines, 2-space indents, no times, no job ids; ids stay in the debug log). LC / MC / CC have "Header PgUp" / "Header PgDn" toolbar actions (pages cycle).
- `AuctionWinnerAnnouncement` now carries `ComponentSources` (the LCs whose ports the drones dock at) and `JobType`. What a winner gets is still only its task list (`TaskAssignments[drone]`); LCs find their part by the DockAt tasks at their connectors, the announcer by the job id.
- Header: active jobs (awarded, drones not all reported), open auctions, jobs held for no LC / no drone (until the hold sent to the announcer ends).

## LCD app (2026-10-08)

- The server draws nothing. LCDs show an Automata block's header + log through the text surface script `AutomataLogApp` ("Automata Log", id `AutomataLog`, `Util/AutomataLogApp.cs`), which runs on each client only. The player sets BOTH: the LCD's content to the app, and the block's LCD tag in the LCD's name (same construct / owner or faction; drones: own grid only).
- Flow (`Util/LcdFeed.cs`): the app sends `LcdSubscribePacket(panel)` every ~5 s; the server (`LcdFeedServer.Subscribe`) checks text panel + content = app + tag in the name + construct + owner, remembers the viewer 15 s (`LcdFeeder`), answers `LcdAttachPacket` (which block, or why not) and the block's last feed. While a block has viewers its `UpdateLcd` (`BlockLcd.Wanted` / `Publish`; drones `DroneStatusDisplay.Flush`) builds the header and the newest 40 log lines and sends an `LcdFeedPacket` when the content key changed (hash, no allocation). No viewers: nothing is built, nothing is sent, no panel scans.
- The host / single player gets the feed objects directly (no network). Header paging is still server side (`LcdStyle.HeaderPage`, synced settings).

## Tool orientation ranking (2026-10-11)

Weld / grind auctions in gravity carry `ToolFaceWeights` (top / side / bottom access of the job's blocks, CC). `ToolReach` scores each bid's best tool over that mix, per face kind: 3 = the tool points into the face with the drone level (top: tool down), 2 = reachable by tilting within its pitch / roll (top with a forward tool needs ~40°), 1 = needs upside down and `CanFlyInverted`, 0.5 = needs more tilt than its limits, 0 = upside down and can't. The value is added to the bid score (scores 0..1, profile bonus 10): the kinds rank above distance / cargo, below the profile. A bid whose value is 0 (nothing of the job reachable) is left out. Tool elevations per face kind mirror the drone's approach tilts (top -90..-40°, side -50..17.5°, bottom 10..90°).

## Mining award (2026-10-11)
`AssignMine`: pooled drones as before (OffsetSite); per drone, ice jobs: `IceTakerFor` (generator) then `RefineryTakerFor` (higher refinery tier than the ice taker's, most room on a tie) as a second Dock + Unload. Mining/CLAUDE.md, mining workflow.

## Shared sites (2026-10-11)
`IsSharedSite` (QueueWinner): weld / grind awards to several drones, or near (60 m) another weld / grind job in `activeJobs` with drones out, get `AuctionWinnerAnnouncement.SharedSite`; `ActiveJob` keeps the job type and position. Drone side: Drone/CLAUDE.md, shared job sites.

## Shared sites: mining (2026-10-11)
`IsSharedSite` also flags MineOre awards (split over drones, or another mining job with drones out within 60 m); build and mine jobs don't share with each other.


## Welder profiles by weld kind (2026-10-11)
`BehaviourProfile` IsWelderRepair 4096 / IsWelderProjected 8192 / IsWelderUnbuilt 16384 (IsWelderDrone is "Welder:All"). `WeldCategory` (byte, JobTypeExtensions.WELD_*: 1 repair, 2 projected, 3 unbuilt, 0 unknown) goes JobAnnouncement 19 -> Auction 23 -> AuctionWinnerAnnouncement 14 -> Job 8 -> JobStatusReport 12. `WeldProfileBonus`: own kind 20, Welder:All / Welder + grinder / unknown kind 10, another kind 5 (steps wider than tool reach 0-3 and bid score 0-1). Logs and events use `JobType.Label(weld)`.

Bid 25 `DockedConnectorId`: `PortFor` -> `DockFit.Pick(..., dockedAt)` takes the connector the drone is docked at before any other port (a job at the station it sits at needs no new dock).

## Active gauge (2026-10-11)
Header line "Active Drones: N, LC: N, CC: N, MS: N": `seenEntities` (id -> type, last tick) is touched by a bid, a job announcement (allowed owners only) or a drone's job report; entries older than `SEEN_TICKS` (30 min) are pruned in `Prune`. Not persisted, not exact: a gauge for players.

## Recall at a marker (2026-10-11)
Designator recall (green): the aimed drone's grid first (as before); none of the player's drones there: `OrchestratorBlock.RecallOrderAt` takes the confirmed non-combat order whose GPS marker is within 8 m of the hit (`RECALL_MARKER_RADIUS` in `ScoutPointer`). Jobs of the order not taken yet are cancelled (queue, auctions, `knownJobs`); drones working the others are recalled (`DroneControllerBlock.RecallJobs(announcer, jobIds)`: `jobAnnouncerId` + `currentJob.JobId`), their "recalled" report ends the job and, with the last, the marker. No hit (open space): error as before.

## Drone keepalives (2026-10-11)
Subscribes to JOB_KEEPALIVE; keepalives for its own jobs (designator orders) feed `droneWatch` (`Util.DroneWatch`, also in CC / MS / LC) and the active gauge. 60 s unheard: "Drone NAME has not been heard of for 60s!" (event, log, broadcast controller when on). Final reports end the watch.
