# Drone Controller definition

- The **Drone Controller** is the first entity of the modthe player is exposed to, it should be cheap, and accessible early game.
- The **Drone Controller** is also the only entity capable of fulfilling tasks.


## Operation modes
- **ManagedByPlayer**: The player can give tasks to the drone through the CustomData of the block, in a simple format. When the player clicks the Execute button on the drone Terminal, it will attempt to parse the task list and perform it.
    - Next to the `Execute` button there should be a checkbox that reads `Repeat task list`, that, when checked, would prompt the drone to repeat the same task list.
- **ManagedByScheduler**: The drone will idle until it receives messages in the `ORCHESTRATOR_BID_ROUND_START` channel.
- **StandAlone**: The drone will perform simple tasks on its own, namely
    - Welding 1 block
    - Grinding 1 block
    - Mining ore

## StandAlone operation
- **Stand-alone behaviour**: A drone with a drone controller block should be able to perform simple jobs like welding, grinding, and mining.
- **Simple behavior of embedded classes**: When a drone embes a **Construction Computer**, a **MiningSurveyor**, or a **Logistics Computer**,  the classes should be purposedly limited in their scope. The gameplay reason is that the Drone has limited processing capacity.
- **Purposely dumb**: Drones performing simple tasks are likely to collide in their tasks:
    - Example 1: Drones A and B operating in solo-mode, may scan a block needing welding at the same time, and both will try to weld it, only for one of them to find that it was already welded by the other drone.
    - Example 2: Drones with mining setting and capabilities, when in solo mode, don't respond to quotas, only filters for 1 ore are allowed. They simply "see ore, mine ore", potentially overfilling containers, or crashing with other drones trying to mine the same resource.
- **Healthcheck**: Even in solo-operation mode, drones should still respect their settings for H2 and battery levels, stop, and recharge when needed

### Example flows

#### Drone finds ore to mine

- When a Drone's embedded **Mining Surveyor** caches ore location, the drone should immediately create the shortest task set that gets the ore to the grid its connected to:
    - Start at connector
    - Go to mining location
    - Mine ore
    - Deposit ore at the connector
    - Repeat

#### Scenario: Drone finds a block to build

- **Trigger conditions**: Drone must have a welder, drone must have some cargo capacity, drone performed a scan of the grids is connected to, and found a block to build.
- **Simple behaviour**: First block that needs building will get built immediately.
- **Conditions for fulfillment**: 
    - **Inventory**: Does one of the conveyor networks in the grid the drone is connected to contain the needed materials to build?
    - **Capacity**: Can I carry all or some of the materials
- Then goes and welds the block


## Thrust & Environment Logic
- **Atmospheric**: Ion thrusters = 0% effectiveness; Atmospheric thrusters = 100%.
- **Space**: Atmospheric thrusters = 0% effectiveness; Ion thrusters = 100%.
- **Hydrogen**: Works in both, but requires monitoring `H2Level`.
- **Rovers**: If `Capabilities` include `Wheeled`, navigation must prioritize surface pathfinding over 3D flight.

## Cargo & Mass Calculations
- **Planetary**: Max load is limited by thrust-to-weight ratio. Use the player-configured `MaxLoadKg` as a hard cap to respect design intent.
- **Zero-G**: Max load is limited only by `CargoVolume`.
- **Logic**: When reporting to an Orchestrator, a drone must calculate its "Current Max Cargo" based on gravity and remaining thrust.

## Capabilities and self-awareness

- **Capabilities check**: Drones should be able to perform a self asessment by inspecting the blocks in its own grid, and determinin what they can do. Refer to `ImprovedAI\Data\Scripts\ImprovedAI\DroneController\Drone.cs` for drone capablities
- **Dimensional Awareness**: drones must understand their physical "footprint" to avoid getting stuck trying to navigate in tight spaces.
- **Profile Definitions**:
    - **Player Input**: Player selects one simple geometric option (Rectangle, Triangle, Oval) for each view, via terminal for **Top, Side, and Front** views.
    - **Drone self assessment**: understand its own geometry and its faces by analyzing itself
    - **Normalization**: 
    - Rectangle: Full width/height.
    - Triangle: 50% width at top, 100% at bottom (tapered).
    - Oval: 80% of bounding box (rounded corners).

## Pathfinding

- **Pathfinding manager**: The drone must interact with the **Pathfinding Manager** to find its way to a check point.
- **Separation of concerns**: The pathfinding manager handles the heavy lifting of the pathfinding operations, the Drone should worry how to get there, and ask the **Pathfinding Manager** if it cannot reach the current waypoint.

### Pathfinding Hardware Simulation
Drones "see" the world via simulated raycasts, but only if they have the correct hardware or if server settings allow it.

#### Short-Range (Sensors: < 50m)
- **Constraint**: If `requireSensorsForPathfinding` is TRUE, drones without a functional Sensor block are "blind" to obstacles within 50m.
- **Implementation**: Raycast in 6 directions from the sensor block location, NOT the controller.

#### Long-Range (Cameras: 50m - 1000m)
- **Constraint**: If `requireCamerasForPathfinding` is TRUE, drones require cameras to "look ahead" for `DirectPathfinder` or `AStar`.
- **Logic**: Use @PathfindingContext to check `HasCameras()`. If no camera exists in the desired travel direction, the path is considered "Unknown/Blocked."

#### Server Admin Overrides
- Admins can designate specific block types to act as "Simulated Sensors" to save performance.
- Admins can toggle `allowAStar`. If FALSE, `PathfindingManager` must fallback to `DirectPathfinder`.

### Settings Hierarchy
1. **Server Bounds**: `globalMinWaypointDistance` and `globalMaxWaypointDistance` are hard limits.
2. **Player Intent**: Players define `DesiredWaypointDistance` and `MinAltitudeBuffer` in the terminal.
3. **Validation**: The `PathfindingContext` must always use the CLAMPED value: 
   `Context.WaypointDistance = Clamp(Player.Value, Server.Min, Server.Max)`.

### Removal Policy
- **Altitude**: Do NOT use server-wide altitude buffers. This is a per-drone player setting.
- **Hardware**: Always check `requireSensors` or `requireCameras` before allowing a drone to transition from `Direct` to `AStar` pathfinding.