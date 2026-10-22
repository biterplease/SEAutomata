# SE Virtual Networking vs Multiplayer Sync
This mod uses a two-layer communication system. You must distinguish between **Simulation Logic** (Gameplay) and **Multiplayer Syncing** (Technical).

## 1. Simulated MessageQueue (Simulation Layer)
The `MessageQueue` simulates the Inter-Grid Communication (IGC) system for `DroneController`, `AIScheduler`, and `LogisticsComputer`.

- **Antenna Rules**: Follow the "Passive Receive" rule: A grid can **RECEIVE** messages if it is within range of a broadcaster, even if its own transmit range is 0. However, it can only **SEND** if the target is within its own set range.
- **Ownership/Visibility**: 
    - Check `IMyCubeBlock.OwnerId` and `MyRelationsBetweenPlayerAndBlock`.
    - Communication is only allowed if the relationship is `Faction`, `Owner`, or `Friends`.
    - Enemy entities **should not** be able to receive broadcasted messages.

## 2. Multiplayer Synchronization (Network Layer)
Because the `MessageQueue` runs in real-time, it must stay in sync across all clients in a multiplayer session.

- **The Sync Bridge**: Even though the queue is "simulated," changes to the `MessageQueue` state must be broadcast using `MyAPIGateway.Multiplayer`.
- **Payloads**: Use `ProtoBuf` for serializing payloads (as seen in `Message.cs`).
- **Implementation**: 
    - Refer to @SE-NanobotBuildAndRepairMod for how to send `byte[]` packets to specific clients.
    - Use `RegisterSecureMessageHandler` on a dedicated `ushort` channel to listen for network updates.

## 3. Strict Constraints
- **No Direct IGC**: Do not use the game's `IGC.SendBroadcastMessage` directly for drone logic; always route through our custom `MessageQueue` to maintain the simulation's state.

## Delivery rules (2026-10-15, replaces 2026-10-02)
- **Synchronous, nobody is looked up.** `BroadcastMessage(GridAntennaReg sender, msg, enforceCommsRange, senderType)` appends to the channel's log, recording the sender's grid, owner and block type (no position: reach is evaluated at read time). No subscriptions, no recipient list, no dead-letter queue, no `RequiresAck`.
- A message lives `LifetimeTicks` (config `MessageRetentionTicks`, default 600 = 10 s). `ReadMessages(readerId, readerType, GridAntennaReg reader, channel, ...)` returns what is alive, of the reader's type (`RecipientBlockType`), allowed (owner / faction), and **reachable at that moment** (`Relays.Reaches(senderGrid, readerGrid, owner)`). Out of range at the time of the read = missed. Each reader has a cursor per channel: it reads a message once, others still read it.
- Send-time feedback is gone: `Message<T>.Receipt` / `.Delivered` (at least one reader took it) tell later. Flows that need delivery resend by themselves once the last copy expired (`MessageQueue.InFlight(msg)`): job announcements (LC / CC / MS: `Pending` / `Announced`), orchestrator winners / failures, drone job reports and survey scans. Keepalives, bids and auction rebroadcasts are best effort.
- `ReadMessages` has early exits that cost nothing (no registration, no scan, no allocation) and return `ErrorCode.TryAgainLater`: shutdown aside, the channel has nothing newer than the reader's cursor (covers an empty channel), or everything newer has already expired (the cursor jumps to the end). It also returns it when a scan took nothing. `GridAntennaReg.Ensure` just looks up the block's grid entry in the relay graph.
- A block closing calls `ReaderClosed(id)` (drops its cursors).
- Channels and payloads of the job handshake: `TODO/10-handshake.md`.

## Channels added (2026-10-06)

- `JOB_WITHDRAWN`: announcer -> orchestrators, JobStatusReport (announcer, job id, reason): drop the job's queue entry /
  auction.
- Removed unused config keys: `[MessageQueue] SchedulerMessageThrottlingTicks`, `DroneMessageThrottlingTicks`
  (never read; drones / computers read their channels every update).

## Relay graph, indexed by grid (2026-10-16, replaces the per-antenna graph)
- Server only, in-process: no packet is involved.
- `GridAntennaReg` (`GridAntennaReg.cs`) is the plain-data entry of **one grid**: name, id, owner, `IsStatic`, `RadioAntennas` (id -> `RadioAntennaData`) and `LaserAntennas` (id -> `LaserAntennaData`), all protobuf. The relay graph owns and refreshes it (`Refresh` reads the grid's antenna lists); blocks only ask for it (`GridAntennaReg.Ensure(queue, ref reg, block)` -> `Relays.Touch(grid, owner)`) and pass it on sends and reads. A grid with no antenna has an entry too.
- Node = **mechanical group** (rotor / piston / hinge), leader = the grid with the most blocks (ties: lowest id). Only the working radio antenna with the largest range of a group counts; every connected laser antenna counts.
- Edge A <-> B: radio, the two best radios are within each other's range (distance <= the smaller range, so symmetric; no passive receive; owner / faction allowed); or laser: an antenna of A is connected to one of B's (both ways, any distance). `enforceCommsRange` is moot (kept in the message for the save format).
- Same mechanical group = always reachable, no antenna needed. So Automata blocks on one grid (or mechanically joined grids) talk without an antenna, and `EnsureAntenna()` of every block is true whenever the queue exists (antenna fields only drive displays / capability text).
- Docked drones: a connector-connected grid is **not** part of the node (LCs still do not read its inventory), but a sender or reader that is a Drone Controller also counts as being on the groups its grid is connected to (Logical group minus its own mechanical group), no antenna needed. `MessageQueue.DOCKED_DRONE_TALKS_TO_ALL` = true: any block type; false: only Drone <-> Orchestrator. The docked groups are cached, the Logical group's events and `GROUPS_REFRESH_TICKS` keep them current.
- Refresh: entries refresh every 60 ticks while touched, antenna lists rescan every 30 s (a new antenna takes up to that long to count), world scan every 60 s finds unregistered grids. Reach = lazy BFS per (start node, owner), cached by topology counters plus `REACH_TTL_TICKS`.
- Saved with the message queue (`Automata_Messages.bin`, `Grids`).
- Drone Controller with an Orchestrator / Logistics / Mining Surveyor / Construction Computer on its mechanical group: the controller errors (`CheckCapabilities`, LCD alert "System Overload: <block> found on grid") and does nothing until the computer is gone; the computers keep working. Otherwise the drone would bid on / be assigned its own jobs.

## Payloads are not encoded (2026-10-15)
- A message in flight holds the sender's payload object itself (`TimestampedMessage.Payload`): no encode on send, no decode on read, one instance shared by the sender and every recipient. The `MessageSerializationMode` setting (XML / ProtoBuf) is gone.
- So **payloads are read-only from the moment they are sent**, on both sides: a sender builds a new payload to change anything (auction rebroadcasts resend the same one), a receiver copies what it wants to change (`Job` takes its own task list: `JobAnnouncement.ToJob`, the drone's `new List<Task>(tasks)`). Audited 2026-10-15: nobody writes to a payload or its inventories / task lists after sending or receiving. `JobAnnouncement.FromJob` copies the job's task list.
- The only encoding is the world save: `MessageQueue.Serialize()` (session `SaveData`, server) and `Deserialize(bytes)` (session `Init`, server), file `Automata_Messages.bin` in the world storage. Payloads are encoded by type tag (`DecodePayload` mirrors `GetPayloadType`: add a new payload class to both). Message ages are kept across the save (with a 10 s life few survive it).
- Blocks no longer save their messages in flight (the `Outbox` of their save data is gone; the orchestrator's `Winners` / `Failures` are not messages yet and stay).

## Directed messages (2026-10-21)
- `Message<T>.RecipientId` (0 = everyone): `ReadMessages` skips a message for another reader before the type mask / share / range work; saved (`SavedMessage` ProtoMember 21). `AuctionWinnerAnnouncement.RecipientId` carries it too.
- `PLAYER_ALERT` / `PlayerAlert` (PayloadType 32768): chat line to the orchestrator. `JobStatusReport.LastPosition`: keepalive position.
- LC job announcements are cached (`Cached`, rebuilt only when the dock / position / static flag changed).
- All `Log.Info` / `Log.Warning` are `Log.Debug` (single-line `if (Log.DebugEnabled) Log.Debug(...)`, so `deploy_local_nolog` strips them); `Log.Error` stays.
