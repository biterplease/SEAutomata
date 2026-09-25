# IAI Construction Computer

- The **Construction Computer** scans the grid or area of the vicinity its installed for construction jobs.

## Embedding

The **Construction Computer** class may be embedded inside:
- An **Orchestrator**, in order for the **Orchestrator** to fulfill the scanning of construction jobs. If embedded in an Orchestrator, it should have the `OperationMode` `BuiltInToOrchestrator`.
- A **Drone Controller**: A simple **Drone** should be able to scan for, and fulfill, simple construction tasks on its own, i.e. welding and grinding.
    - When the **Construction Computer** is embedded into a **Drone**, and its operation mode set to `BuiltInToDrone`, it should be limited to single tasks only:

## Construction job types
- **WeldBlock**: Weld a block with needed components. The block may be **Unfinished**, **Damaged**, or **Projected**.
- **GrindBlock**: The Construction Computer defines, a specific color in its setttings. Any block that has that color, should be marked for Grinding.