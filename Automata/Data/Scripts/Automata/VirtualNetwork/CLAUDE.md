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
- **Synchronous, nobody is looked up.** `BroadcastMessage(AntennaReg sender, msg)` appends to the channel's log, recording the sender's position, range and grid. No subscriptions, no recipient list, no dead-letter queue, no `RequiresAck`.
- A message lives `LifetimeTicks` (config `MessageRetentionTicks`, default 600 = 10 s). `ReadMessages(readerId, readerType, AntennaReg reader, channel, ...)` returns what is alive, of the reader's type (`RecipientBlockType`), allowed (owner / faction), and **in range of the reader at that moment** (sender's radio range, or both ranges when `enforceCommsRange`; or the relay graph reaches the reader). Out of range at the time of the read = missed. Each reader has a cursor per channel: it reads a message once, others still read it.
- Send-time feedback is gone: `Message<T>.Receipt` / `.Delivered` (at least one reader took it) tell later. Flows that need delivery resend by themselves once the last copy expired (`MessageQueue.InFlight(msg)`): job announcements (LC / CC / MS: `Pending` / `Announced`), orchestrator winners / failures, drone job reports and survey scans. Keepalives, bids and auction rebroadcasts are best effort.
- `ReadMessages` has early exits that cost nothing (no registration, no scan, no allocation) and return `ErrorCode.TryAgainLater`: shutdown aside, the channel has nothing newer than the reader's cursor (covers an empty channel), or everything newer has already expired (the cursor jumps to the end). It also returns it when a scan took nothing. The reader's reg is registered (relay graph updated) only past the early exits. `EnsureAntenna` / `AntennaReg.Ensure` only tells the queue about a new or switched antenna.
- A block closing calls `ReaderClosed(id)` (drops its cursors).
- Channels and payloads of the job handshake: `TODO/10-handshake.md`.

## Channels added (2026-10-06)

- `JOB_WITHDRAWN`: announcer -> orchestrators, JobStatusReport (announcer, job id, reason): drop the job's queue entry /
  auction.
- Removed unused config keys: `[MessageQueue] SchedulerMessageThrottlingTicks`, `DroneMessageThrottlingTicks`
  (never read; drones / computers read their channels every update).

## Antenna registration and the relay graph (2026-10-15)
- Server only, in-process: no packet is involved in registering or messaging.
- `AntennaReg` is the plain-data registration (antenna entity id, kind radio / laser, position, range, grid is static; plus owner, grid id, working, laser link / target, names). No block references: the block, if ever needed, is `MyAPIGateway.Entities.TryGetEntityById(AntennaId)`. Blocks keep one (`AntennaReg.Ensure(queue, ref reg, antenna)` each update) and pass it on every send and read; the relay graph keeps **one entry per antenna id** (an orchestrator, LC, CC and MS can share an antenna).
- Static grids' entries are not updated by blocks (a switched antenna excepted); mobile ones (drones, mobile bases) are updated only by the reg that comes with a send / read. There is no periodic refresh of mobile entries: the 60 s fallback rebuild is the only other update (it re-reads every entry). `AntennaReg.Sync` polls `CubeGrid.IsStatic`, so static -> mobile is picked up at the next call (`IMyCubeGrid.OnIsStaticChanged` exists but is not needed). Static antennas' range / position edits are picked up by the 60 s fallback rebuild, which also finds antennas no block registered and drops lost ones.
- `RelayGraph`: nodes are logical grid groups with a working broadcasting radio antenna or a connected laser antenna; radio edges by range (directed), laser edges both ways at any distance (the game's own link `IMyLaserAntenna.Other`, else matching target coordinates, owners allowed for each other). Reach is a lazy BFS per (start group, owner), cached while the topology is unchanged (moves are re-read after 120 ticks). Messages beyond the sender's radio range go out when the graph reaches the reader (`IsInCommunicationRange`).
- The graph's antennas are saved with the message queue (`Automata_Messages.bin`), so it is not rebuilt from scratch at load.
- Limitation: every Automata block registers through a working, broadcasting **radio** antenna on its own mechanical group; a base with only a laser antenna can relay for others but its blocks are not on the network.

## Payloads are not encoded (2026-10-15)
- A message in flight holds the sender's payload object itself (`TimestampedMessage.Payload`): no encode on send, no decode on read, one instance shared by the sender and every recipient. The `MessageSerializationMode` setting (XML / ProtoBuf) is gone.
- So **payloads are read-only from the moment they are sent**, on both sides: a sender builds a new payload to change anything (auction rebroadcasts resend the same one), a receiver copies what it wants to change (`Job` takes its own task list: `JobAnnouncement.ToJob`, the drone's `new List<Task>(tasks)`). Audited 2026-10-15: nobody writes to a payload or its inventories / task lists after sending or receiving. `JobAnnouncement.FromJob` copies the job's task list.
- The only encoding is the world save: `MessageQueue.Serialize()` (session `SaveData`, server) and `Deserialize(bytes)` (session `Init`, server), file `Automata_Messages.bin` in the world storage. Payloads are encoded by type tag (`DecodePayload` mirrors `GetPayloadType`: add a new payload class to both). Message ages are kept across the save (with a 10 s life few survive it).
- Blocks no longer save their messages in flight (the `Outbox` of their save data is gone; the orchestrator's `Winners` / `Failures` are not messages yet and stay).
