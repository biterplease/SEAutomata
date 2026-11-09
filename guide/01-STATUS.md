# Automata: Player Guide

## Current status, what's implemented, what's planned

This mod is still very early in development. While some of its features work, a lot of it still needs polishing, namely:
- Welding and grinding: works 80% of the time, there are times where drones get stuck and need manual rescue, or simply burn all their battery/fuel trying to navigate to a block that they should be able to without issue
- Mining is inefficient: because stone gets in the way, drones mine A LOT of stone before they can actually bring some useful ores
- Timing issues: in order to reduce effect on simulation speed, actions are often throttled and deferred, this leads to getting feeling that things are not happening at all.

The above said, welding, grinding, mining, logistic deliveries, ore scouting, combat, as well as the docking, takeoff, navigation, and healthcheck of the drone, work at acceptable levels, but I'm still working to make them better and more reliable.

### Still untested:
- Server stuff: multiplayer infrastructure is there, and used everywhere in the mod, but it's only ever been tested locally
- Moving grids: while the drones are expected to work with reference beacons on moving grids, has not been tested yet

### Planned features:

- Quarry mining: mining drones HAVE to be borers, i.e. their hull needs to fit the hole that their drills make. With Quarry mining, a drone with a single drill to be able to clear its own area to maneuver and mine successfully.
- Farm harvesting jobs: drone with a collector harvests farmed ores.
- Rovers: for the really massive cargo move jobs, rovers should be available.
- Mod config menu: live update server settings
- Models: LG drone controller, all computers, still using vanilla models, custom models with custom actions are underway.
