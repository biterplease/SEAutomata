**Automata** lets you create drones that assist you in mining, logistics, and construction.

Place a **Drone Controller** on a drone of your design, and whatever tools you want it to use (welder, grinder, drill, ore detector, cargo), and the drone figures out by itself what it is able to do. A single, solo drone can already weld, repair and grind around its home on its own.

The mod introduces 5 blocks that lets you scale your activities, 3 of them specialize on checking for a specific types of jobs, and the **Orchestrator** collects those jobs, and hands them to the drones best suited for them.

Blocks on the same grid (or rotor / piston / hinge connected grids) talk without an antenna. Between grids, everything talks over your antennas: a broadcasting radio antenna on each grid within range of the other, or laser antennas linked to each other. Docked drone are able to talk to the grid its connected without an antenna.

The mod adds six blocks and two hand-held tools:

- **Drone Controller**: The workhorse, that allows a drone to check its own capabilities, and sign up to do the work distributed by the Orchestrator. It's the only block that actually does work: weld, grind, deliveries, mining.
- **Orchestrator**: Analyzes incoming job announcements, groups them, if needed, turns them into job auctions, picks the winning drones and gives them their orders. One network is enough.
- **Logistics Computer**: Analyzes inventory on its grid, keeps player-configured item min/max in its memory, and dispatches jobs to keep those amounts healthy. A grid needs one to take part in logistics.
- **Construction Computer**: Scans a large area for blocks to grind, weld, or repair, and announces them as jobs.
- **Mining Surveyor**: It collects ore deposit data and turns ore orders into mining jobs.
- **Target Designator**: a block that points a camera and sends the designator orders (see below).
- **Industrial Target Designator** and **Hostile Target Designator**: hand-held tools to send drones somewhere by pointing: ore scouting with the industrial one, combat with the hostile one.

## Example use cases


### 1. Distant mining outpost

You've already built your base on the EarthLike, and the closest ice lake is 12km away.

You can build a drilling rig at the ice lake, and connect it however you prefer to your main base, then setup an `Ore/Ice:10000:0` order at the main base.

A drone with either behavior profiles of **Cargo Drone** or **Ore hauler** will go and collect the ore.

Note: you need Logistics Computers on both the main base and the drill rig.

### 2. Welding

Tired of welding your own stuff?

Configure a Construction Computer, which has a HUGE construction field, to observe your base.

Once you setup your welder drone, simply placing the block will prompt the drone to build it, given that you have the materials.

If you setup a projection, as long as there is at least 1 weldable block, the drone will start and weld the entire projection with a 90% success rate.

### 3. Mobile shipyard

If you have multiple projects, you can simply put your materials, and construction drones, in a mobile ship. It will build anything that ends up inside the construction area.

### 4. Combat drone mothership

Frustrated with setting up docking and takeoff for combat drones? No more!

Any drone that has:
- AI Offensive block
- AI Defensive block
- AI Flight
- Hopefully, weapons too

Will **automatically** undock and defend you when a threat is detected.

Or you can send them ahead with the **Target Designator Block**, from up to 15km away!

The target designator block needs a Camera, and with that camera, you can use toolbar actions to mark targets and engage.

You want a specific, or specific drones to go into combat? No problem, simply TAG them first, then choose your target.

The recommended setup is:
- Setup rotor, hinge, and camera.
- Custom Turret Controller, set it up with the rotor, hinge, and camera
- Target designator: select same camera as the CTC
- Setup your toolbar from a cockpit
- ENGAGE the target

Alternative setup:
- On a drone, place the Target Designator block.
- Standard remote control
- Camera, configure the cam in the target designator block
- Configure toolbar actions of of the Target Designator
- TAG your combat drones with your designator drone
- ENGAGE the target

