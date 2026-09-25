# Mining Surveyor definition

Any **Drone Controller** could be equipped with an Ore Detector, and, if the player has the setting on the **Drone Controller**, it should collect ore findings, and report the ore findings to the **Mining Surveyor**. The **Mining Surveyor** should cache this location and ore data, and report to the player. If the **Mining Surveyor** has the `ScanAndPublish` `WorkMode` enabled, it will then broadcast a Mining Job.

The **Mining Surveyor** may be equipped in a grid that contains an ore detector, in which case it would cache the data itself for those nodes that it can detect.

## Embedding

The **Mining Surveyor** class may be embedded inside an **Orchestrator**, in order for the **Orchestrator** to fulfill the scanning, and caching, of ore locations. 