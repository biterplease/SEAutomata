## Experimental alpha build

The mod *mostly* works, but there's some features that need polishing and cleanup. Some features might be tagged as experimental, and might get removed later on.

# Automata

Automatons to make your life easier.

This mod enables you to configure drones to perform tasks that are commonly performed by players.

Logistics driven gameplay is encouraged, as you now can have a number of independent grids, that your drones will keep in sync, as configured by the Automata Computer blocks.

Automata Computer blocks perform analysis, and they are your gateway to management; but a drone is required to do any "Actual work". You can give a preference for certain types of jobs, based on the Drone's **Behavior Profile**.

What "actual work" means, is activities like:
- **Logistics**: `Cargo drone` or `Ore hauler` move inventory between 2 completely separate grids.
    - **Cargo drone**: Prefers logistics that don't involve carrying ore. Needs a standard connector.
    - **Ore hauler**: Prefers logistic jobs that only involve Ores or Ice. Can use both a large or a small connector.
    - **Tanker (planned, not implemented)**: Gas hauler, takes on jobs to move Hydrogen or Oxygen.
- **Mining surveys**: **Ore Scout** drones can fly an area and survey for ores. Can do "Spot checks" as well as "Area sweeps"
- **Mining**: **Mining drones** mine ores or ice. Can do "spot checks" for ore surveys.
- **Construction**: Welder or Grinder drones can, well, weld or grind. There are 5 construction profiles:
    - **Welder:All**: All kinds of weld jobs
    - **Welder:Projected**: Prefers projection jobs, but can still take repair or unbuilt
    - **Welder:Unbuilt**: Prefers unbuilt block jobs, but can still take projected or repair
    - **Welder:Repair**: Prefers repair jobs, but can still take projected or unbuilt
    - **Grinder**: Grind jobs. Blocks need to be painted with grind color for it to target them.
    - **Construction**: Combination of `Welder:All` and `Grinder`
- **Farming**: `Harvester` drone prefers farming crops from farm plots, but can also take trash pickup
- **Trash pickup**: `Janitor` drone prefers trash pickup, but can also take farming.
- **Combat**: Works with vanilla AI Offensive, AI Defensive, and AI flight, to give your combat drones additional, non-combat capabilities
    - The Drone Controller will handle undocking, out-of-combat navigation, and docking for your drones
    - The drone controller "listens" to the signal from the AI defensive, when it detects an enemy.
    - The drone controller then hands control over to the AI Flight, AI Offensive, and AI Defensive, to take over.
    - Once no more targets are detected, or a signal to flee is received from the AI Defensive, the drone will leave combat
    - (Planned, not implemented) Combat formations: Your combat drones will follow formation to the target's location.

Needless to say, each behavior profile needs the proper tools for the job, i.e. a Mining drone with no drills cannot mine. Most drones need cargo.

For a drone to be able to navigate, it needs:
- Camera: to see obstacles far away, in the direction the camera is pointing
- Sensor: to "sense" obstacles nearby, all around.
- Antenna: to communicate with the computer blocks
- Tools appropriate for the job, i.e. Mining drone needs drills, a Welder drone needs (gasp!) a welder
- Enough cargo for its needs: Harvester drones can get away with a few tiny small cargos, Cargo drones might need larger ones

## Blocks

The mod introduces 5 blocks that lets you scale your activities, 3 of them specialize on checking for a specific types of jobs, and the **Orchestrator** collects those jobs, and hands them to the drones best suited for them.

Blocks on the same grid (or rotor / piston / hinge connected grids) talk without an antenna. Between grids, everything talks over your antennas: a broadcasting radio antenna on each grid within range of the other, or laser antennas linked to each other. Docked drone are able to talk to the grid its connected without an antenna.

The mod adds six blocks and two hand-held tools:

- **Drone Controller**: The workhorse, that allows a drone to check its own capabilities, and sign up to do the work distributed by the Orchestrator. It's the only block that actually does work: weld, grind, deliveries, mining.
- **Orchestrator**: Analyzes incoming job announcements, groups them, if needed, turns them into job auctions, picks the winning drones and gives them their orders. One per network is enough.
- **Logistics Computer**: Analyzes inventory on its grid, keeps player-configured item min/max in its memory, and dispatches jobs to keep those amounts healthy. A grid needs one to take part in logistics.
- **Construction Computer**: Scans a large area for blocks to grind, weld, or repair, and announces them as jobs.
- **Mining Surveyor**: It collects ore deposit data and turns ore orders into mining jobs.
- **Target Designator**: a block that points a camera and sends the designator orders (see below).
- **Industrial Target Designator** and **Hostile Target Designator**: hand-held tools to assign tasks by pointing: ore scouting with the industrial one, combat with the hostile one.

Full guide here https://steamcommunity.com/sharedfiles/filedetails/?id=3817492846
