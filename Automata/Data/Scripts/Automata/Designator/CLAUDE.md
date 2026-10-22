# Target Designator (2026-10-22, `TODO/24-feature-target-designator.md`)

Block: `MyProgrammableBlock` subtypes `AutomataLargeTargetDesignator` (1x1x1) and `AutomataSmallTargetDesignator` (1x1x2), `BlockPairName` `AutomataTargetDesignator`, vanilla Custom Turret Controller model / components / mount points, screens `CockpitScreen_01` with `Script="AutomataLog"`. Logic: `TargetDesignatorBlock : SettingsBlockLogic, INicknamed` (`[MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false, ...)]`), terminal controls in `TargetDesignatorTerminalControls` (vanilla PB controls hidden through `Util/AutomataProgrammableBlocks`). Never embedded; may share a grid with a Drone Controller (not counted in `DroneControllerBlock.ComputerName()`).

## Settings (`TargetDesignatorSettings`, save data `TargetDesignatorSaveData` in the block's mod storage)
`LcdTag` (1, default "[Designator]"), `CameraId` (2), `Nickname` (3), `Lcd` (4, LCD style). No broadcast-controller option: the orchestrator is the only chat writer.

## Terminal / toolbar
Nickname, LCD tag, LCD style, camera dropdown (combobox, key = camera entity id, 0 = none; cameras of the designator's mechanical group, friendly, working at use time; the content callback gets no block, so the controls keep the one last shown). A saved camera that is not on the list shows as none. Toolbar actions `Automata_Designator_{Tag,Spot,Area,Engage,Escort,Recall,Clear}` ("Designator: TAG" ...). A press sends `DesignatorCameraPacket { DesignatorId, Order }` to the server (`ScoutPointer.CameraReceived`, terminal access checked).

## Orders
- `ScoutPointer.CameraReceived` finds the block by `DesignatorId`, raycasts from the camera (`CAMERA_RANGE` 15 km, hits on the camera's own mechanical group skipped) and runs the hand-held path (`ScoutPointer.Order`) with an `OrderSink { Designator, Self = designator.RootGrid() }`. Hand-held designators use `OrderSink { Orch }` (direct `AcceptDesignatorJob`).
- `OrderSink.Submit` builds the `JobAnnouncement` (order fields: `OrderId`, `Identity`, `Kind`, `Marker`, `What`, `RequiredDrone`, `OriginBlockId`) and, for a camera, `TargetDesignatorBlock.SendOrder` puts it on `Channel.DESIGNATOR_JOB_ANNOUNCEMENT` (`RecipientBlockType = Orchestrator`, reach via `RelayGraph` / `GridAntennaReg`). The running orchestrator ingests it (`Orchestrator/CLAUDE.md`, "Designator orders") and answers on `DESIGNATOR_ORDER_UPDATE`: `Received`, `Confirmed` (with the winning drones' line), `Cancelled`.
- `UpdateOrders()` (server, `UpdateBeforeSimulation10`): reads updates addressed to this block into the LCD log; an order unanswered for 5 s gives `ScoutPointer.Feedback(Identity, Error)` and "no orchestrator answered"; pending entries are forgotten after 90 s.
- **Self-escort**: ESCORT with no hit, or a hit within `SELF_ESCORT_RADIUS` (5 m) of the camera, escorts `RootGrid()` (largest-volume grid of the mechanical group). Static construct or not friendly: error. A hit on another friendly mobile grid escorts that grid.
- **No-bid rule**: the order carries the designator's grid (`PlayerJobOriginGrid`), drones on that construct do not bid (`Auction.NoBidGridId`, `DroneControllerBlock.WhyNotJob` / `OnConstructOf`).

## LCD
`BlockLcd lcd` channel 0 = the block's own screens plus LCDs named with `LcdTag`, feed "Automata Log" style: header (title, TAGS, Industrial / Combat counts from `ScoutPointer.TagSummary`, last tagged target, camera line) and the order log (`AddLog`).

## Network
`IAIBlockType.TargetDesignator` (32), `AutomataEntityType.TargetDesignatorBlock` (32); `MessageQueue.InRange` treats it as a drone for docked reach.

## Untested in play
Camera on a rotor head, same grid, detached head, no orchestrator in reach, two orchestrators, tagged drones, area sweep, escort / self-escort, recall, CLEAR, LCD on surface 0, toolbar from a cockpit.
