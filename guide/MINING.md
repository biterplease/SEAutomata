# Mining, and the Mining Surveyor

In order to mine, you need to have in your network:
- An Orchestrator block
- A Mining Surveyor
- An *eligible* Logistics Computer: the conditions for a logistics computer to accept ore from mining jobs is either
    - Have a refinery that can handle the job ore. For example, a Basic Refinery cannot handle platinum, therefore it will not be eligible.
    - If the ore mined is Ice, it needs an O2/H2 generator
    - OR, have the work mode "Accept ores/ice", which enables it to accept ores or ice without refineries or O2/H2 generators


Once the above is setup, you need to trigger two actions:
1. Surveying for ores
2. Placing a mining order

## Ore surveys

To survey for ores, you'll need a drone equipped with an Ore Detector, configured to the "Ore Scout" behavior profile. Then, by using the handheld Industrial designator, or the Target Designator block with a camera, you can order that drone to an area where you suspect there are ores.

Both methods feature two types of surveying:
- **Spot check**: The drone will simply fly to the target location, and back.
- **Area sweep**: Drone (or drones) will fly to the point and run a sweep of the area.

In either of these methods, any ores detected will be reported to the mining surveyor, making them eligible to mined.

### Tagging

You can TAG drones, in order to send multiple drones for a single survey job. Multiple drones on the job perform the task a lot faster and a lot wider. There is no real benefit of tagging multiple drones for SPOT checks, but for area sweeps, there is a significant increase in area covered and completion time. Up to 8 drones can be tagged in a single area sweep.

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

| Drones | Orbits | Orbits* per drone |
| --- | --- | --- |
| 1 | 4 | 4 |
| 2 | 4 | 2 + 2 |
| 3 | 4 | 2 + 1 + 1 |
| 4 | 4 | 1 each |
| 5 | 5 | 1 each |
| 6 | 6 | 1 each |
| 7 | 7 | 1 each |
| 8 | 8 | 1 each |

*Asteroid shapes are irregular, so Drones don't follow actual orbits, they follow along the surface of asteroids, flying close to it. Imagine you tied a rubber band around the shape of the asteroid, that is the path that a drone flies

## Placing a mining order

Using the terminal of the Mining Surveyor, you can place mining orders for known ores.

Drones will dig until they are close to the ore, and then start drilling.

Mining may take a long time, its because the drone's mine stone around the target ore, and don't dispose of it. Because a) Stone gets consumed very quickly by refineries, and b) there are mods, plugins, other solutions to stone, and this mod does not attempt to fix "the stone problem".

Expect drones to mine stone alongside other ores. For this reason, starting a mine shaft will be slow at first, but the order for ores will start filling up really quickly once the stone layer gets consumed.

## Drone mining modes

Drones, in their mining section, have 2 modes, **Bore (default)** and **Quarry**.

### Bore

**Bore** is the default drilling method, and is expected to be used by drones that can **fit their entire hull in the hole dug by their drills**. Boring is faster than **Quarry**, but is less safe, and multiple drones digging their own shafts tends to be problematic.

### Quarry

With **Quarry** mining, the drone will first dig a shaft, using its Quarry Dimension setting, to prepare the area before any drilling takes place.

This method is ideal, for:
- Hotspots with many ores
- Drones that cannot bore

The **Quarry Dimension** is the length of the sides of the square-shape that the shaft will be. Larger values give the drone more space to maneuver, but take more time to dig. The drone always uses a minuimum size that is safe for its hull to maneuver in.

Multiple drones working nearby in quarry mode, will work together to dig the shafts.

Quarry mode will protect ore voxels, so drones will stop digging a shaft when their drills are within 7.5m of the ore. You can ignore the protection of certain ore voxels by adding said ores to the drone's IGNORE list.


## Designator job types

Both the **Ore Scout** and the **Mining** behavior profiles are eligible for SPOT checks. The system will always prefer ore scouts if no drones are tagged, but a tagged mining drone will honor the job order for a SPOT check.

Only Ore Scouts will perform Area Sweeps.

### Quarry preparation

If one or more mining drones are tagged, and ordered somewhere with an AREA SWEEP order, it will begin a **Quarry preparation**, which will prompt the drone(s) to dig shafts.

Repeated fires in AREA SWEEP mode at the same location, will increase the depth of the shaft. These jobs will always use the largest Quarry dimension of all of the drones.

Example:
3 drones tagged, Quarry dimensions of 7, 8, 10. The largest value of 10 will be used.

If you send one AREA SWEEP order, the shaft size will be `10*10*10`.

If you send 3 AREA SWEEP orders, the shaft size will be `10*10*30`, 30 being depth.

If there are ores found in the path of the shaft, the digging of the shaft is stopped.

**If you setup the drone to IGNORE specific ores, and those ores are found in the shaft, the drone will destroy it and dig through it.** This is true for regular mining in Quarry mode, and Quarry Preparation jobs.

Quarry Preparation jobs can be canceled by using the Industrial designator, or the Designator Block Camera, on RECALL mode.

## Drone's Ore ignore list

Drones that have Ores setup in their IGNORE list will not bid on jobs that require mining said ore.