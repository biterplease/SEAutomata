# Drone Controller

## Behavior profiles

The behaviuor profile tells Orchestrators what this drone *prefers* to do. A drone still bids on any job its equipment allows, but drones with a matching profile always win over the others.

| Profile | Preferred jobs | Notes |
| --- | --- | --- |
| Cargo drone | Item deliveries and pickups between Logistics Computers | |
| Ore hauler | Ore and ice deliveries | Takes **only** ore / ice deliveries (and mining, if it has a drill). Can dock with small connectors. |
| Welder:All | Weld jobs of every kind | |
| Welder:Repair | Weld jobs, repair first | Ranks first for repair jobs (damaged blocks), and bids on them first when several auctions are open. Still takes the other weld jobs, ranked below Welder:All. |
| Welder:Projected | Weld jobs, projections first | The same, for projected build jobs. |
| Welder:Unbuilt | Weld jobs, unbuilt blocks first | The same, for unbuilt jobs (placed blocks not welded up yet). |
| Grinder drone | Grind jobs | |
| Welder + grinder drone | Weld and grind jobs | |
| Mining drone | Mining jobs | Needs a drill and an ore detector. |
| Ore scout | Industrial designator scouting | The **only** drones that take area sweeps, and the first choice for spot checks (any drone with an ore detector can take those). Needs an ore detector. Can dock with small connectors. |
| Combat drone | Hostile designator engage / escort | The **only** drones that take combat jobs. Needs AI Defensive, AI Flight and AI Offensive blocks. Can dock with small connectors. |
| Harvester | Crop farming | Drone with collector that prefers harvesting crops, but can pickup trash if needed. |
| Janitor | Trash collection | Trash collection, but can farm crops if needed. |

You can also stop a drone from bidding with some of its abilities in the **Jobs** section (for example, "no jobs in space" even though it can fly there), and tell mining drones to ignore specific ores.

## Home connector and home beacon

Drones can be configured with a **home connector**. When set, this drone will always return there to dock, recharge, refuel, and drop off leftover cargo.

- Pick one of your (or your faction's) connectors from the **Home connector** list. Use **Scan** to refresh the list, or type a connector's name to find one farther away.
- Best results: dock the drone by hand, then press **Set current as home**. The drone remembers that exact docking position and orientation.
- Give each drone its own home connector. Two drones sharing one will fight over it.
- If the home connector is on something that moves (a carrier, a mining ship), tick **Home connector is not station** and select a **home beacon** on the same ship. The drone uses the beacon to find its way back to the moving home.

Drones with landing gear and no home connector can still park on nearby grids or the ground, and combat drones and ore scouts return to where they took off.

A drone without a home connector clears leftover cargo (grinder salvage, a welder's unused components) at the last connector it loaded or unloaded at, or, if it knows none, at the nearest free connector within 2 km whose conveyor network has cargo room.

## Stand-alone mode

A stand-alone drone works by itself, without any computers or Orchestrator. It watches a small **construction field** around its home connector (up to 10 x 10 x 10 blocks), or centered around a GPS marker instead and:

- welds unfinished blocks, repairs damaged ones, and builds projections,
- grinds down blocks painted with the **grind color**,
- never touches blocks painted with the **weld ignore color**.

It loads the components it needs from the conveyor network of its home connector, does as much as it can carry, comes back, and repeats. You can choose the order it works in (repair, projections, unbuilt). Turn on **Show construction field** to see the box.

Stand-alone drones are lack the capabilities to coordinate large jobs, so two stand-alone drones observing the same area may go for the same block, bump into each other, or crash. For several drones on one project, use the Orchestrator.

In stand-alone mode, the Drone's Weld priority is a strict order: it finishes one kind before starting the next.

## Managed by Orchestrator mode

In this mode the drone waits at home and bids on jobs from Orchestrators in antenna range. This is the mode for logistics, mining, large construction projects, and designator jobs. The drone flies out, does its task list, reports back and returns home. It still watches its battery and hydrogen levels and goes home to recharge when needed.

**Low power** (Monitor battery / hydrogen levels: under the refuel threshold every drone drops its job at once, whatever it was doing (an orchestrator's job is reported as aborted and auctioned again), then docks to charge: at home, if the home connector is set, otherwise at the nearest free connector. Cargo of the dropped job (ore, a haul) stays aboard and goes where that job was originally meant to be unloaded, once the levels are back over the operational thresholds and before the drone takes new work.

Combat drones in this mode also launch on their own when their AI Defensive block spots an enemy within the **auto-launch radius**.

The operation mode can only be changed while the drone's AI is off.

## Economy Flight

**Economy flight** is meant to save fuel on long trips (longer than 1 km), it works as follows:
- In Planetary Gravity: drones fly like a quadcopter drone, by using only the downward thrust and its gyros to adjust its heading. It still uses other thrusters to course-correct if its vectors misalign.
- In Space: The drone will choose the face with highest thrust, accelerate to max speed, then glide. It will turn to face the same highest-thrust group in the direction of travel (to decelerate), and burn down to a full stop when at the destination. It would use its other thruster groups to adjust evctor misalignments.

## Flight lanes

Drones managed by an Orchestrator fly in lanes, like air corridors.

See the [Orchestrator: Flight Lanes](./6-Orchestrator.md#flight-lanes) for more details.

## Pathfinding

The drone interacts with the mod's pathfinder, in order to navigate to where it needs to be.

This happenes in a "fall forward" manner, where it tries the simplest form of navigation first, and if it fails, it increases the complexity of the method, until it succeeds:
1. Known paths are recorded by the pathfinder, and are found immediately if they have been successful. If no known path succeeds, then the following happens:
2. If direct pathfinding: a line from where the drone is to where it wants to be
3. Reposition: reposition to one of: left, right, up, up-left, up-right, retry.
4. Once retries are exhausted, a "proper pathfinding" method takes over, and finds a route for the drone.
5. Once a route is found and is successfully flown, it gets recorded, and re-used in step 1.

Initial pathfinding comes at a cost, but that cost gets reduced significantly once the routes are recorded.

Recorded routes are saved with the world save, so they get reloaded when the server restarts.

Routes are also separated by Faction, and Player, preventing other players from accessing your own routes.

## Custom routes

If you would prefer to define your own routes, you can do it by following the steps below. Tts actually better for server simulation simulation speed!

1. Disable drone AI "Enable AI => OFF"
2. Set the drone to "Managed by player"
3. Enable drone AI "Enable AI => ON"
4. In the drone controller terminal, scroll down to "DEBUG" section
5. Check the box to display the debug options
6. Under "Custom nav", enter the waypoints you want for your route, in order.
7. (Optional) You can set the orientation you want the drone to have to each waypoint, by adding a second GPS point under "Orient From". The drone will orient itself along the vector traced from "Orient from" to "Target".
8. With all waypoints entered, press Navigate

The drone will fly the route you entered, and this route will always be preferred by your drones or your faction's drones.

