# Automata

**Early alpha build**. The mod *mostly* works, but there's a lot of features that need polishing and cleanup.

Automated drones that assist you. 

## Summary

The Drone Controller can be mounted on any grid. It will then analyze the grid to see what its capable of.

Using the behaviour profile you can select what job this drone **prefers**, but it does not exclude them of bidding on other jobs as well. These profiles are implemented, and 2 more are planned:
- Cargo Drone
- Ore Scout
- Welder:All, Welder:Repair, Welder:Projected, Welder:Unbuilt (every welder takes any weld job; the last three rank first for their own kind)
- Grinder
- Welder+Grinder
- Mining
- Combat
- Ore Hauler
- Farming (planned): Harvest farm block crops.
- Rover (planned): Drone with this setting would prefer to use its wheels instead of thrusters.

Drones have an operation mode, which can be one of:
- Stand alone: drone works by itself, with no collaboration. Its work-area is small, but flexible.
- Managed by Orchestrator: Drone receives orders by the orchestrator block (more on that later).
- Managed by player: mostly for taxing or debugging. This mode may not survive the development cycle.

There are 4 blocks added by the mod, besides the Drone Controller:
- Logistics Computer: analyzes inventory and enables logistics.
- Construction computer: observes a large area and orders builds/grinds jobs for anything it finds.
- Mining Surveyor: accepts mining surveys from drones, and orders Mining Jobs for known deposits.
- Orchestrator: the brain of the operation, manages job announcements, and distributes work efficiently among drones.

These 4 blocks is how activities are planned at large (though the drone can do a lot by itself too).

### Job types summary:

All the blocks added by the mod enable several job types to be undertaken by the drones, here is a summary:
- Weld jobs: Welder drone is instructed to go to a known Logistics Computer connector, pick up materials, and go weld one or more blocks at a location. Weld jobs come in three kinds: Repair (damaged blocks), Projected (projections) and Unbuilt (placed, not welded up); the Construction Computer publishes all three at once, sharing its job slots by its Weld priority.
- Grind jobs: Grinder drone is instructed to go grind one or more block at a location, then go to a know Logistics Computer connector to drop off the materials.
- Inventory delivery: Cargo drone is instructed to pickup inventory from a Provider Logistics Computer, and deliver it to a Requester Logistics Computer
- Ore Scouting: triggered by the player, a Ore Scout drone can be ordered to scout an area for ores. Mining drones can do this too.
- Mining: Mining drones is instructed to go to a known deposit, and mine specified amounts of ore.
- Combat: triggered by the player, a Combat drone will approach an area, and delegate control to its AI Offensive, AI Defensive, and AI flight blocks

Complete guide at 