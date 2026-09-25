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