# ImprovedAI

Logistics and Construction drones for Space Engineers.

This mod introduces versatile AI blocks that allow you to automate distributed logistics.

## Use-case examples

- Faraway mining outpost, delivers ores to a different base via drone or truck.
- Factory bases ship components to other bases.
- Mining barge can handle a swarm of mining drones.
- Manage a swarms of construction drone to build projections.
- Deliver cargo to friendly bases.
- Deliver special payloads to enemy bases.

## Features

- Server first design: control research 

### Drone controller

The most basic component, the drone block will enable a grid with AI features, allowing the drone to complete simple construction tasks, like fetching and delivering goods.

The drone block is for both large grid and small grid, and will analyze the grid is mounted on to determine what its capable of. Note that the drone block itself needs other blocks to act.

One drone on its own is a powerful tool, and can handle a wide range of tasks, like:
- Building projected blocks, if it has a welder
- Grinding blocks, if tagged with the appropriate color, if it has a grinder
- Mining ores, if it has an ore detector and drill
- Scouting for ores, if it has an ore detector
- Delivering goods, given enough cargo
- Fetching goods, given enough cargo

These are just some examples, refer to the wiki for a full list of tasks.

Drones have 1 downside, and its that they cannot communicate with other drones, this is what the Orchestrator is for.

### Orchestrator

The Orchestrator serves as the brain for drone fleets, handling the scanning of tasks, and assigning tasks for inventories.

Consider the complex logistics of building a projection in location A, when the materials are distributed over several locations. The orchestrator will analyze inventories, search for available drones, and coordinate the drone swarm to efficiently complete the job.

Usually only 1 orchestrator is needed per player, as long as the grids are connected via radio or laser antennas.

### Logistics Computer

The logistics computer is an assistive block that analyzes inventories and reports them to the Orchestrator. Its what allows the logistics network to understand what inventories it has, and where.
