# Logistics Computer

The Logistics Computer is in charge of watching for inventory changes, and maintaining a stock of items at the grid its mounted on.

When configured, the Logistics Computers on your grid will talk to the rest of the network, and have drones deliver missing items (below min), or pick up any surplus (above max).

The Logistics Computer itself bids with its available items, and available volume, to interact with the rest of the logistics network, if configured to do so.

It has notably, 5 settings that are core to its functioning:
- **Provide for logistics**: The logistics computer ill offer items or volume to fulfill other Logistics Computers requests.
- **Provide for construction**: The Logistics Computer will offer items for weld jobs, or cargo space for grind jobs.
- **Request**: It will automatically request items that go below their configured minimum.
- **Push**: It will automatically push  items that go above their configured max, out to the logistics network,
- **Accept mined ore**: Normally, Logistics Computers will only offer its cargo space for Ore mining jobs if it has blocks that consume the ores: Basic or Full Refinery. When its checked, it will accept ores regardless if it has a block to consume it.
- **Accept mined icd**: Normally, Logistics Computers will only offer its cargo space for Ice mining jobs if it has blocks that consume the ice: O2/H2 generators, or Irrigation Systems. When its checked, it will accept ice regardless if it has a block to consume it.
- **Ore requests mine**: Normally, ore/ice is treated like any other item. When this box is checked, the Logistics Computer will start a Mining Job to fulfill its need for ore/ice.
- **Harvest crops**: Harvest crops from farm blocks that are 100% ready. Only observes the grid that the Logistics Computer is mounted on.

Logistics computers never watch inventories outside of its own mechanical construct. This means:
- Grids connected via connector are not watched
- Grids connected via an advanced rotor, piston, hinge, or merge block, ARE watched

## Conveyor networks

A conveyor network is a group of at least 1 connector and 1 cargo container.

The Logistics Computer sees its grid as one or more **conveyor networks**, and it identifies each separately within a grid.

Each conveyor network is mapped with its own:
- Storage Capacity: cargo containers
- Inputs: Connectors and collectors
- Outputs: Connectors

It maps refineries, reactors, O2/H2 generators, etc. Everything that holds an inventory is accounted for.

When bidding for jobs, it only uses the items or volume in cargo containers, refinery OUTPUT, or assembler OUTPUT, it never offers up:
- Uranium inside a reactor
- Ice inside an O2/H2 generator
- Anything in the input of an assembler
- Anything in the input of a refinery
- Bottles inside a gas tank.

When a Logistics Computer interacts with the rest of the logistics network, it knows which specific connector and cargo to send it to, drones are directed to specific connectors.

Conveyor networks that are missing connectors, or cargo containers, are ignored, and cannot be interacted with.

## How to configure it

1. Setup: Place the Logistics Computer on the grid, with either an antenna that can reach a grid with an orchestrator, or an orchestrator on the same grid. Remember that for a conveyor network to be valid, it needs at least 1 cargo container and at least 1 connector.
2. Enable/disable the work modes you want.
3. (Optional) Select the Conveyor Network you want to place the order on. The CN tagged with [M] is the main one.
4. (Optional) Use the filter to search for an item
5. Set the item's you want MIN:MAX using the forms.

And you're done!

## Behavior with the min/max setting

### Example 1: You want a 1000 steel plates in this grid
- You setup with `SteelPlate:1000:0`
- When the count of steel plates goes below 1000, it will wait 10 seconds,and then an order is placed to reach 1000 Steel Plates
- If available in the network, it will be delivered
- No excess plates are pushed away as surplus, because the maximum is 0.

### Example 2: You want a minimum, and a maximum
- Setup with `SteelPlate:1000:2000`
- The **midpoint** of this setting is `1500` i.e. `MIN + [(MAX - MIN) / 2]`
- When the count of steel plates goes below 1000. After 10s, an order is placed of `1500 - current_count`, in order to bring it to the **midpoint**.
- If the setting was `SteelPlates:1000:5000`, the midpoint would have been `1000 + [(5000-1000)/2] = 3000`
- If the count goes above `2000`, after 10s, a push request is made to the network to pick up the surplus , attempting to bring the count to 1500 (`current_count - 1500`)

### Example 3: You want a maximum
- Setup with `SteelPlate:0:2000`
- When the count of steel plates goes above `2000`, after 10 seconds a push request is made, of `current_count - 2000`, to bring the count back to `2000`plates, and remain 
- No plates are ever ordered, because the minimum is 0.


## Practical example:

Typically, you would want to separate the irrigation systems from the O2/H2 generation system.

One way to do it, is to keep entirely separate networks, and keep separate Ice requests on each.

The image above shows 2 Conveyor Networks in a single grid, completely separate from each other. Each has a request for Ore/Ice:20000:0, and each has checked "Ore/Ice requests mine".

This triggers mining jobs for Ice, to be delivered to it. Since it does not have any refineries, any stone generated from the mining gets delivered elsewhere.

