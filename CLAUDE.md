# Space Engineers Automata Mod: Context & Rulesproject Project

This mod, **Automata**, or **IAI** aims to provide the player with tools to create and automate logistic networks. It features the following elements:
1. **Construction Computer**: Scans an area for unbuilt, damaged and projected blocks and announces the jobs (an Orchestrator auctions them). A block of its own; the only embedding is the **DroneControllerBlock**'s, for its stand-alone weld / grind work.
2. **Logistics Computer**: Required for a grid to take part in logistics: maps its conveyor networks, keeps item Min / Max (terminal lists), announces DeliverMissingInventory / CollectInventorySurplus jobs, bids its items / room on auctions. Never embedded.
3. **Mining Surveyor**: Parses and caches mining data and announces mining jobs. A block of its own, never embedded.
4. **Orchestrator**: Only reads job announcements, builds the task lists, runs the auctions, picks the bidders and announces the winners (asynchronously, over the Virtual Network). It scans nothing itself and embeds nothing.
5. **Drone Controller**: Performs tasks based on its own capabilities. It **embeds** a construction computer for stand-alone operation, allowing it to complete simple construction tasks.
6. **Virtual Network**: Simulates a two-layer communication system that allows the other elements of the mod to communicate with each other. Enforces antenna ranges, and ownership and access checks. See [Automata\Data\Scripts\Automata\VirtualNetwork\CLAUDE.md](Automata\Data\Scripts\Automata\VirtualNetwork\CLAUDE.md)

## Code structure

Each entity is defined in a set of files, as follows:
- **<Entity-directory>**: Usually a directory contanining entity files
  - **<Entity>.cs**: A file with same name as directory`.cs`, defines enums and classes relevant to the entity.
- **<Entity>.cs**: Defines the core logic of the entity. If the entity is an embeddable entity, i.e. its class will be embedded in a different block type, this file should be present.
- **<Entity>Block.cs**: The ModAPI hooks to the game (`MyGameLogicComponent`) AND the entity's logic: LogisticsComputerBlock, OrchestratorBlock and ConstructionComputerBlock hold their logic directly (no separate logic class). `ConstructionComputer.cs` stays a class: the scanning / job engine, embedded by the drone and used by ConstructionComputerBlock. Blocks with player settings derive from `Util/SettingsBlockLogic` (XML in mod storage, synced whole with `BlockSettingsPacket`).
- **<Entity>Settings.cs**: Player-controlled settings for the block/entity. These settings must be displayed in the blocks' terminal.
- **<Entity>TerminalControls.cs**: Hooks for hiding default controls, and setting up specific controls for each block.
- **Other files**: there may be other files that don't adapt to this convention, grouped simply because its relevant to the entity in question.

## Core Constraints
- **Language**: C# 6.0 features ONLY (No records, no top-level statements).
- **Communication**: Internal-to-the-mod message queue, that simulates antenna communications by checking signal ranges and antenna ownership.

## Space Engineers coordinate system
- **Up/Down**: +Y / -Y
- **Right/Left**: +X / -X
- **Back/Forward**: +Z / -Z

## Gyroscope Control (Relative to IShipController)
- **Yaw**: Negative = CounterClockwise (towards -X); Positive = Clockwise (towards +X).
- **Pitch**: Negative = Nose Down (-Y); Positive = Nose Up (+Y).
- **Roll**: Negative = CounterClockwise around Z; Positive = Clockwise around Z.


## SE Virtual Networking vs Multiplayer Sync
This mod uses a two-layer communication system. You must distinguish between **Simulation Logic** (Gameplay) and **Multiplayer Syncing** (Technical).


## Reference Docs

The [`./docs`](./docs/) directory contains several useful reference repos:
- **ModAPI**: Keens official Mod API documentation is cloned locally at `docs/SpaceEngineersModAPI/api/`. Each type  as its own HTML file named after its fully-qualified type name (e.g., `VRage.Game.ModAPI.IMyCubeGrid.html`). To look up an API type, read the corresponding HTML file in that folder. Use `xrefmap.yml` in the same directory to find the filename for a given type UID.
- **SEInventorySorter**: A different mod implemented as a client-only plugin. Cloned locally at `docs/SEInvontorySorter`.
- **Nanobot-Build-and-Repair-System**: is a wildly successful mod that implements the Space Engineers client/server protocol, and a large amount of features. Cloned locally at `docs/211SE-Nanobot-Build-and-Repair-System`
- **SE-ModScript-Examples**: A collection of ModScript examples. Cloned locally at `docs/SE-ModScript-Examples`

## Performance-First Development (Server-Side)

Because this mod will be dealing with expensive logic (**pathfinding**) efficiency is the absolute priority. Code must be written to minimize GC pressure, CPU spikes, and main-thread blocking.

### 1. Zero-Allocation & GC Management
- **Avoid Linq**: Never use `.Where()`, `.Select()`, or `.Any()` in update loops. Use `for` or `foreach` on pooled lists.
- **No String Interpolation in Loops**: Do not use `$"Value: {x}"` inside `UpdateAfterSimulation`. Use cached `StringBuilder` or pre-defined strings.
- **Ref Structs & Passing by Ref**: Use `ref` or `in` for large structs like `Vector3D`, `MatrixD`, and `PathfindingContext` to avoid copying data on the stack.
- **Primitive Collections**: Prefer `MyConcurrentDictionary` or `MyConcurrentQueue` from `VRage.Collections` for thread-safe operations.

### 2. API Specific Optimizations
- **Square the Distances**: Always use `Vector3D.DistanceSquared` instead of `Vector3D.Distance` for comparisons to avoid unnecessary `Math.Sqrt` calls.
- **Self Throttling**: The **UpdateAI** method of each entity, should consist of state-machine that decides the next action based on its current state, and should avoid bundling too many actions together.
- **Caching**: Entity classes should cache and reuse its own locals, to prevent unnecessary allocations.

### 3. Logic Branching & Shortcuts
- **Early Exits**: Place the cheapest checks (booleans, enums) at the top of a method. If `!IsEnabled`, return immediately before doing any math.
- **Instruction Counting**: If logic is complex, spread it across multiple frames using a state machine or a counter (`if (tick++ % 10 == 0)`).
- **Bitwise Enums**: Use `[Flags]` enums for `WorkModes` and `Capabilities` to perform lightning-fast capability checks using `&` operators.

### 4. Serialization
- **Serialization formats**: all entities should be serializable to both **XML** and **ProtoBuf**: **XML** would be used to debug, but **Protobuf** must be default, to optimize read/writes.
- **All Entities** should have the proper decorators, in order to be saved/loaded when the game engine requires it, this includes:
  - Entity state
  - Entity player configuration
  - Entity server configuration
  - Entity's MessageQueue inboxes and outboxes
- **Non-entities that need to be saved**: The **IAISession** component, **MessageQueue**, and **Config** are implemented as singletons, and need to be serialized and saved/loaded as well.
- **Pathfinding**: **Pathfinding** is unique to **Drone Controller**s, but pathfinding results of a series of expensive operations, and the results must be cached, and serialized for save/load, in order to avoid recomputing.

## Blocks (2026-10-02)
- Drone controller: `RemoteControl` subtype, large and small. Every remote control control / action is hidden on it except On / Off and the generic terminal fields (whitelist in DroneControllerTerminalControls).
- Orchestrator, Logistics Computer, Construction Computer, Mining Surveyor: `MyProgrammableBlock` subtypes (LCD support) with the broadcast controller model (`Models\Cubes\Large\Broadcast.mwm`), large grid only. The programmable block's Edit / Argument / Run / Recompile controls and Run actions are hidden on them (`Util/AutomataProgrammableBlocks`, subtype prefix "Automata"); On / Off, generic fields and LCD controls stay. SBC ScreenAreas: `AutomataScreen_01` / `AutomataScreen_02` (the custom model's screen materials must use these names). Changing the orchestrator from BroadcastController to MyProgrammableBlock breaks orchestrators placed in older saves.
- Only standard connectors are ports: `ConveyorNetwork.IsStandardConnector` (vanilla small subtypes, or a 1-cell small-grid connector, are small). A drone with a small connector is in error; a logistics computer ignores small connectors (warning).

## Item keys (2026-10-02)
`Inventory/ItemKeys`: a component's key is its subtype ("SteelPlate"); anything else is "Type/Subtype" ("Ingot/Iron", "Ore/Iron", "PhysicalGunObject/WelderItem", "AmmoMagazine/NATO_25x184mm"). `DiscreteInventory.AddItems(IMyInventory)`, `AutomataSession.componentLookup` (all physical items, mass / volume), the drone's load / unload, logistics jobs and Min / Max entries all use these keys. `AutomataSession.itemCatalog`: listed items sorted Components, Tools, PlayerWeapons, PlayerAmmo, BlockAmmo, Ingots, Ores; `TryResolveItem` finds an item by key, subtype or display name (ambiguous names such as "Iron" need the key).

## Debug logging
Debug level (server config) logs the virtual network (registrations, every broadcast with its recipient count and why others were skipped, reads), the orchestrator (announcements, bids, why an auction waits, trips, winners), logistics computers (networks, jobs, bids per network, awards), construction computers (scans, jobs, awards) and the drone's loads / unloads. Prefixes: `VNet:`, `Orchestrator <id>:`, `LC <id>:`, `CC <id>:`, `MS <id>:`, `Drone <id>:` (combat: `Drone <id>: combat:`).

## Persistence through a world save (2026-10-02)
One save struct per block, protobuf, base64, prefix `AS1:` (`Util/BlockSave`), in the block's mod storage under `AutomataSession.MOD_GUID` (the only storage key). Written whenever the game builds the block's object builder (IsSerialized: world save, streaming, copies) and when settings change; runtime parts are the server's only. Applied at load once item keys are indexed and the message queue is up (`BlockSave.CanRestore`); times stored relative (ticks ago / seconds left). Storage without the prefix is the older settings-only format (read as settings).
- `LogisticsComputerSaveData`: settings; state = request / push jobs, bid promises per network, counters, outbox.
- `ConstructionComputerSaveData`: settings; state = cached jobs (blocks, announcement, awarded / hold), counters, outbox, planner sweep frames of its grids.
- `MiningSurveyorSaveData`: settings (orders, server-written known-ore list); state = deposits, deposits found depleted (1 h), order progress, jobs, counters, outbox.
- `OrchestratorSaveData`: state = inbox (queued announcements, bids of open auctions), open auctions, known jobs, unsent winners / failures, counters, outbox.
- `DroneSaveData` (also the custom component data): settings; job (task list + index, resumed at the start of its task group; outstanding bid); operating state (learnt work tilt, thruster baseline, refuel flags, unreachable blocks, planner sweep frames, ore scans not delivered); combat handover state; the award behind its job and job reports not heard yet; outbox.
- Every save struct records the block's `EntityId`: a copy (paste, blueprint, projection) has a new id and drops the runtime state (`BlockSave.OwnState`). Settings are never written before the storage was read (`SettingsBlockLogic`). Members whose default is not 0 / false are `IsRequired`.
- Outbox = the block's messages still in flight on the virtual network (`MessageQueue.CollectSent` / `RestoreSent`, ages kept); every message is saved once, by its sender. Inbox = received but not yet processed (only the orchestrator has one: its queue and auction bids; the others act on what they read at once). Messages whose sender isn't registered (yet) stay queued until they expire.
- Not saved, rebuilt: routes (re-planned on resume: 0.08 ms median, 0.8 ms p95 over 1261 logged routes) and the pathfinding scene (`LocalScene`: ~1 us per block to build, kept 30 min unused within a session).

## Job handshake (2026-10-02)
See `TODO/10-handshake.md`: announce, queue (orchestrators dedupe between them), auction, winner / failure (no bid, dropped, refused: all told), drone outcome reports (`JobStatusReport` on TASKS_COMPLETED / TASKS_ABORTED) that close or reopen the announcer's job. Messages are delivered once per recipient, to subscribed blocks only.
