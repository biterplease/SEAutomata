# Logistics Computer & Conveyor Network Logic

The **Logistics Computer** is a Large-Grid exclusive block responsible for managing inventory quotas and interfacing with the **Orchestrator**. It treats the grid not as a single container, but as a collection of isolated or interconnected **Conveyor Networks**.

For its operation and , see `ImprovedAI\Data\Scripts\ImprovedAI\LogisticsComputer.cs.`
- **ProvideForLogistics**: Make its inventory available for logistics requests by other **Logistics Computer**s.
    - Logistic request will fulfill inventory at a different grid, making the inventory of this logistics computer's grid available
- **ProvideForConstruction**: Only construction tasks will be able to request inventory from this **Logistic Computer**
- **Push**: Will push inventory to the Logistic Network. If an item quota is set, it will respect it. Will not provide inventory for any tasks.
- **Request**: Will request inventory from the Logistic Network. If an item quota is set, it will respect it. Will not provide inventory for any tasks.

## Embedding

The **Logistics Computer** class may be embedded inside an **Orchestrator**, in order for the **Orchestrator** to manage conveyor networks, and virtual inventory. 

## 1. Conveyor Network Mapping
- **Definition**: A "Conveyor Network" is a group of inventory-holding blocks (Cargo Container, Connector, Collector, Refineries, Assemblers, Reactors) that can physically exchange items.
- **Connectivity Logic**: 
    - **CRITICAL**: Use the indexed documentation for `IMyInventory.IsConnectedTo`. 
    - Implementation must call `bool IMyInventory.IsConnectedTo(IMyInventory other)` to validate if two blocks share a physical path.
- **Mapping Protocol**:
    - Iterate through all inventory blocks on the grid.
    - Group blocks into a `ConveyorNetwork` object if `IsConnectedTo` returns true.
    - **Caching**: Only re-run this "Grid Crawl" when `OnBlockAdded` or `OnBlockRemoved` is triggered for the grid. Do not run this per-frame.
- **Inventory Nodes**:
    - **Connectors**: Bi-directional I/O. These are the primary interaction points for Drones.
    - **Collectors**: Input-only. Items entering here must be mapped to their destination Network.
    - **Cargo Containers**: Internal storage nodes.
- **Update Strategy**:
    - Perform a full grid crawl to group inventories into `List<ConveyorNetwork>` only when the LogisticsComputer is functional, or when the grid structure that the Logistics Computer is built on changes (`OnBlockAdded`/`OnBlockRemoved`).
    - Cache these networks to avoid expensive connectivity checks every tick.

## 2. Inventory Quotas & Validation
- **Scopes**: Quotas can be set by the player, for either a **Specific Container**, a **Conveyor Network**, or each individually.
  - The terminal controls in space engineers are rather simplistic, as such, the quotas saved by the logistics computer consist of simple strings such as "SteelPlate:100", that consist of "<item readable name>:amount"
  - The mod, upon load, builds a dictionary of all item slugs to item readable names.
  - Logistics computer must satisfy its own quota **first**, before fulfiling requests by its managing Orchestrator.
- **Validation**: 
    - Sum the `MaxVolume` of all containers within a Conveyor Network.
        - I/O blocks (Connectors and Collectors) should not be summed despite having an inventory, since that volume is needed as a staging area in order to move inventory through the I/O block.
    - If `Quota > MaxVolume`, update terminal `DetailedInfo` to notify the player of an "Impossible Quota."
- **One-Time Requests**: 
    - The LogisticsComputer monitors its CustomData. If a player enters a request string (Item, Amount) and triggers the "Send Request" action:
    - The LogisticsComputer generates an immediate `InventoryRequisition` or `TaskAnnouncement`.

## 3. Orchestrator Interaction (The Handshake)
- **Push (Surplus)**: If a Network's inventory exceeds a quota, the LogisticsComputer broadcasts a job of type `CollectInventorySurplus`.
- **Pull (Deficit)**: If a Network's inventory falls below a quota, the LogisticsComputer LogisticsComputer broadcasts an job of type `DeliverMissingInventory`.
- **Mutual Blacklisting**:
    - Both LogisticsComputer and Orchestrator maintain a `List<long> BlacklistedIds` (persisted in Settings).
    - If an ID is blacklisted, ignore all incoming messages/requests from that entity.

## 4. Performance Guardrails
- **Throttled Scanning**: Use events to check when inventory changes if possible. Use a `lastInventoryScanFrame` timer to ensure inventory volumes are only polled only when no update event has been issued, after 1800 ticks (30 seconds).
- **BFS Optimization**: When mapping networks, start the search from Connectors and move inward to Cargo to prioritize access points for drones.

## 5. Operation modes

### OperationMode Enum

The `OperationMode` (byte) defines how the Logistics Computer interacts with the Logistic Network and handles inventory provisioning.

| Value | Member | Description |
| :--- | :--- | :--- |
| `0` | `None` | No inventory operations. |
| `1` | `ProvideForLogistics` | Supplies items for logistics tasks only. |
| `2` | `ProvideForConstruction` | Supplies items for construction tasks only. |
| `4` | `Push` | Pushes to the network. Respects quotas. Does NOT provide for tasks. |
| `8` | `Request` | Requests from the network. Respects quotas. Does NOT provide for tasks. |

## Usage Rules
- **Provisioning vs. Transfer**: `Provide` modes (1, 2, 4) are for task fulfillment. `Push`/`Request` modes (4, 8) are for network-level balancing.
- **Quotas**: All modes should respect item quota settings.
- **Exclusivity**: When `Push` or `Request` are active, the entity should not provide items for jobs.