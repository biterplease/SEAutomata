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

## Delivery rules (2026-10-02)
- Recipients are fixed at send time: blocks of the recipient types that are **subscribed to the channel**, sharing with the sender, inside the sender's antenna range.
- A non-direct message is delivered **once per recipient** (it is removed from the message's recipients when read) and kept until it expires for the others; nobody re-reads a message.
- `UnsubscribeAll` when a block closes. Channels and payloads of the job handshake: `TODO/10-handshake.md`.

## Channels added (2026-10-06)

- `JOB_WITHDRAWN`: announcer -> orchestrators, JobStatusReport (announcer, job id, reason): drop the job's queue entry /
  auction.
- Removed unused config keys: `[MessageQueue] SchedulerMessageThrottlingTicks`, `DroneMessageThrottlingTicks`
  (never read; drones / computers read their channels every update).

## Relay graph and laser antennas (2026-10-14)
- `RelayGraph`: nodes are logical grid groups with a working broadcasting radio antenna or a connected laser antenna; radio edges by range (directed), laser edges both ways at any distance. A laser edge needs the game's own link (`IMyLaserAntenna.Other`, set while Connected; the target coordinates only when neither end reports it) and owners allowed for each other. Messages beyond the sender's radio range go out when the relay graph reaches the recipient (`MessageQueue.IsInCommunicationRange`); a player is covered by any of their radio antennas whose node reaches the base. Log: "VNet relay: N node(s), M edge(s), L connected laser antenna(s), E laser edge(s)".
- Limitation: every Automata block registers through a working, broadcasting **radio** antenna on its own mechanical group; a base with only a laser antenna can relay for others but its blocks are not on the network.
