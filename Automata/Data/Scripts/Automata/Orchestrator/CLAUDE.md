# Orchestrator Architecture
The `Orchestrator` is the central brain for task distribution.

## Operation
- **Full Solution**: The **Orchestrator** is a  should embed its own **Construction Computer**, **Mining Surveyor**, and **Logistics Computer**, in order to perform all the scanning of different jobs, and managing virtual inventories.
- **Never embedded**: while the **Orchestrator** embeds the logic of other entities, it is never embedded into anything.

## Core concepts
- **Job**: Is something that needs be completed, from the perspective of the player
- **Task**: Is one or more intermediate steps needed to complete a job. Tasks are distributed in sets, that are a sequence of steps needed in order to complete the job.
- Refer to `ImprovedAI\Data\Scripts\ImprovedAI\Orchestrator\Orchestrator.cs` for definitions for the different types of **Job** and **Task**.


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