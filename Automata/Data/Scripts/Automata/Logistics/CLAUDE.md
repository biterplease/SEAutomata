# Logistics Computer & Conveyor Network Logic

The **Logistics Computer** is a Large-Grid exclusive block responsible for managing inventory quotas and interfacing with the **Orchestrator**. It treats the grid not as a single container, but as a collection of isolated or interconnected **Conveyor Networks**.

For its operation, see `LogisticsComputerBlock.cs` (logic and block in one class).
- **ProvideForLogistics**: Make its inventory available for logistics requests by other **Logistics Computer**s.
    - Logistic request will fulfill inventory at a different grid, making the inventory of this logistics computer's grid available
- **ProvideForConstruction**: Only construction tasks will be able to request inventory from this **Logistic Computer**
- **Push**: Will push inventory to the Logistic Network. If an item quota is set, it will respect it. Will not provide inventory for any tasks.
- **Request**: Will request inventory from the Logistic Network. If an item quota is set, it will respect it. Will not provide inventory for any tasks.

## Embedding

Never embedded: a grid needs its own Logistics Computer block to take part in logistics.

## Min / Max (implemented, 2026-10-02)

Terminal lists, saved in `LogisticsComputerSettings.Quotas` as "item:min:max" (`QuotaEntry`; item = inventory key, see root CLAUDE.md "Item keys"; 0 = not set). Top list: every catalog item without an entry, as "item:0:0" (Components, Tools, PlayerWeapons, PlayerAmmo, BlockAmmo, Ingots, Ores). Bottom list: the entries. Selecting puts the entry in the edit box; buttons: Edit and add (top selection -> bottom), Edit (replaces the bottom selection), Remove, Add manually (typed entry; unknown item or bad numbers: nothing happens), Clear all. One entry per item. Request mode: below a Min the difference is announced (DeliverMissingInventory). Push mode: above a Max the excess is announced (CollectInventorySurplus). Min > Max is a loop: warned and ignored. Min / Max apply to the main network (most cargo room with a free standard connector), where deliveries arrive and surplus leaves; providing from it never goes below its Min / above its Max.

## Networks and ports (2026-10-02)

Every conveyor network of the grid group (mechanical) is mapped. A bid comes from the network that gives the most (supply) / takes the most (room) and names one of its free standard connectors (`IOLocationData`); promises are tracked per network (key: its lowest block entity id). Small connectors are not ports (warning in the info panel and log).

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

## Gas, Ice, keepalive (2026-10-06)
- Conveyor networks also map O2/H2 generators, oxygen / hydrogen tanks and reactors (`gasGenerators`, `gasTanks`, `reactors`).
- Ice (MineOre, logistics of Ice only): to a network with a working O2/H2 generator when one has a free port, else any network with room; never needs a refinery.
- `JOB_KEEPALIVE` from the drone refreshes an awarded job (long multi-trip deliveries outlived the 5 min award TTL and were announced twice); reports for awards never heard count.

## Promises with several drones / dropped auctions (2026-10-06)

- A won commitment records the drones of the award that dock at this LC's connectors (`Commitment.Drones`); each
  drone's report removes its id, the promise ends with the last one (restored saves without the set: first report).
- `JOB_ANNOUNCEMENT_FAILED` with this LC's bid auction (any announcer, also announcer 0) releases a promise not yet
  won at once, instead of holding the room / items for `COMMIT_TTL` (5 min).
- A job of its own is finished only when its last drone reports (`DronesOut`).

## Mining bids (2026-10-11)
MineOre bids carry `RefineryTier` (best working refinery of the bid network: 3 prototech, 2 refinery / industrial, 1 basic, by subtype name) and `GasGenerators`. Ice with no O2/H2 generator at the computer: the network with the best refinery is bid (the drone also brings stone / other ores; the orchestrator may send those here). Mining/CLAUDE.md, mining workflow.
