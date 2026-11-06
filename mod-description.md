## Experimental alpha build

The mod *mostly* works, but there's a lot of features that need polishing and cleanup. Some features tagged as "experimental" might get removed entirely.

# Automata

Automated drones that assist you.

## Features
- Automated drones!
- Logistics oriented gameplay: no more massive bases, create small hubs with puprose, your drones will deliver for you.
- Weld anything: whether projected, hand-placed, or simple repairs, your welder drones will fix it.
- Grind: tag anything with the grind color and watch it go away.
- Mine: send your drones to scout for ores, and mine them!
- Fight: control your your combat drones to engage an enemy, or escort a friendly.

Full guide here https://steamcommunity.com/sharedfiles/filedetails/?id=3817492846

## TL;DR

### The blocks:

- **Drone controller**: does all the work. Besides the expected tools of any drone, it needs
    - **A camera**: to detect obstacles in its flight path
    - **A sensor**: to detect obstacles nearby
    - **An antenna**: to communicate with the network. Range can be small (20m-50m), as long as it can reach its "boss" the orchestrator.
- **Orchestrator**: coordinates all the work.
- **Logistics Computer**: tells the orchestrator what is available. Needed for inventory access.
- **Construction Computer**: tells the orchestrator what needs to be built or ground.
- **Mining Surveyor**: collects ore survey data and tells the orchestrator what needs to be mined.
- **Target designator**: grid version of the handheld Industrial/Hostile Target Designator, with 15km range.

### The network:

- Blocks on the same grid need no antenna.
- To form a network between grids, they need to be within antenna range of each other.
- Laser antennas work too!
- Docked drones talk to the grid they are connected to.
- Grids need to belong to you, or your faction

### To weld:

1. Build a basic welder drone, add a Drone Controller. Set behavior profile to `Welder:All`.
2. Set a drone's construction field through its home connector and GPS.
3. You can visualize the area in the terminal or using `/automata debugdraw dronenav`.
4. Place a block or project in the area, drone will build it, if you have the materials.

### To weld, bigtime

1. Build several basic welder drones, add a Drone Controller to each. Set behavior profiles to `Welder:All`.
2. Place an Antenna, Construction Computer, Logistics Computer, and Orchestrator on a nearby grid.
3. Use the Construction Computer terminal to resize, offset, or visualize its Construction Field. `/automata debugdraw ccobs` also visualizes it.
4. Place a block or project in the area, the drones will build it.

### To grind

1. See **Weld** sections, replace the welders by grinders. Set behavior profile to `Grinder`
2. Select the grind color of the Drone Controller or Construction Computer.
3. Paint things that color, they will be ground.

### To survey for ores

1. Place an ore detector on a drone
2. Place an Antenna, Orchestrator, and a Mining Surveyor on a nearby grid.
3. Make an Industrial Target Designator at any SKit or assembler.
4. Point your Industrial Target Designator in TAG mode to the drone.
5. Point your Industrial Target Designator in SPOT mode to where you suspect the ores are located
6. Your drone will fly there, and gather data. Once the drone is back, it will report to the Minnig Surveyor what is found.

**Notes**:
- Industrial Target Designator has a range of 5 km.
- Using a camera with an Automata Target Designator block has a range of 15km. See the guide for more details.

### To survey for ores, bigtime

1. Place an ore detector on several drones. Set their behavior profiles to `Ore Scout`
2. Place an Antenna, Orchestrator, and a Mining Surveyor on a nearby grid.
3. Make an Industrial Target Designator at any SKit or assembler.
4. Point your Industrial Target Designator in TAG mode to the drones.
5. Point your Industrial Target Designator in AREA mode to where you suspect the ores are located.
6. Your drones will fly there, and perform an area sweep while gathering data. Once the drones are back, they will report to the Mining Surveyor what has been found.

**NOTES**:
- Only drones with the "ore scout" behavior profile can perform Area Sweeps.
- Industrial Target Designator has a range of 5 km
- Target Designator block with a camera has a range of 15 km, but it requires a bit more setup.
- Area sweep on a planet: covers a base area of 100m radius, for each drone added, up to 8, the radius increases 75m (to a max of 625m)
- Area sweep on an asteroid: paths are drawn along the surface of asteroid, and drones follow it. 

**Planet area sweep**

| Drones | Area radius | Area covered |
| --- | --- | --- |
| 1 | 100 m | 31,400 m² (0.03 km²) |
| 2 | 175 m | 96,200 m² (0.10 km²) |
| 3 | 250 m | 196,300 m² (0.20 km²) |
| 4 | 325 m | 331,800 m² (0.33 km²) |
| 5 | 400 m | 502,700 m² (0.50 km²) |
| 6 | 475 m | 708,800 m² (0.71 km²) |
| 7 | 550 m | 950,300 m² (0.95 km²) |
| 8 | 625 m | 1,227,200 m² (1.23 km²) |

**Asteroid area sweep**

| Drones | Orbits | Orbits per drone |
| --- | --- | --- |
| 1 | 4 | 4 |
| 2 | 4 | 2 + 2 |
| 3 | 4 | 2 + 1 + 1 |
| 4 | 4 | 1 each |
| 5 | 5 | 1 each |
| 6 | 6 | 1 each |
| 7 | 7 | 1 each |
| 8 | 8 | 1 each |


### To mine

1. Build a mining drone that can bore, meaning that its hull can fit the hole the drills make.
2. Place an Antenna, Orchestrator, Logistics Computer and a Mining Surveyor on a nearby grid.
3. Survey for ores, the Mining Surveyor needs data to place an order.
4. Use the Mining Surveyor terminal to create an ore order.

**NOTES**:
- Mining orders exhaust themselves as the ore comes in. There is an option to make them permanent, but ore and stone will likely overflow your cargo.
- Stone is mined, not dumped. Due to this, orders are a bit slow, but after a few trips your ores show up.
- You are free to deal with it yourself through any method you like.
- Logistics Computer only accept ores if the grid its on has a refinery, or ice if it has an O2/H2 gen. You need to tick "Accept ores/ice" in order to take them without these blocks.

### To request items:

1. Place an Antenna, Orchestrator, and Logistics Computer on the grid that wants items.
2. Place an Antenna, Orchestrator, and Logistics Computer on the grid that has items.
3. Ensure the grids are either within antenna range, or connected via laser antennas
4. Check the `Request` work mode on the grid that wants items
5. Check the `Provide for logistics` work mode on the grid that wants items.
6. On the grid that wants items' Logistics Computer, configure the `ITEM:MIN:MAX` you want
7. A drone will pick up the items at one grid, and deliver to the other grid.

### To fight:

1. Create a combat drone, place an AI Flight, AI Defensive, AI Offensive, and a Drone Controller on it
2. Configure the "Combat handover" section of the drone
3. If an enemy approaches within the defensive drone's range, your drone will engage it.
4. You can use the Hostile Target Designator to TAG drones and then use ENGAGE mode to send them to attack a target.
6. Use RECALL mode to send them back.

**NOTES**:
- Its the AI Flight, AI Defensive, and AI Offensive doing the fighting, make sure they're configured beforehand
- The Automata Drone Controller only handles takeoff, and travel to target. Once no more targets are detected, the Automata Drone Controller takes over again and returns home.
- Hostile Target Designator has a range of 5 km (15 km from a Target Designator block camera, see the guide). It is made only at the full Assembler.

### To protect:

1. The Hostile Target Designator can tag drones in TAG mode.
2. Point the Hostile Target Designator at another grid in ESCORT mode, the tagged combat drones will follow and defend it.
3. If you point the Hostile Target Designator at the ground near yourself, and fire in ESCORT mode, the tagged drones will follow and defend you instead.
4. After engaging, they will return to escorting.
5. Use RECALL mode to send them back.


### Debug drawing commands

All debug draw commands time out after a short time (as defined in the .ini). Each argument draws a specific aspect of a specific block
Usage:

```
/automata debugdraw [dronenav] [droneobs] [ccobs] [park] [lanes] [off] [range]
```

Where:
- `dronenav` drone navigation path and waypoints
- `droneobs` drone's construction field
- `ccobs` construction computer field
- `park` parking field defined by the Orchestrator
- `lanes` navigation lanes defined by the orchestrator
- `range` Automata entities within this range will accept the draw order. 0-2000 meters, default 300
- `off` turns off all debug drawing, regardless of range
