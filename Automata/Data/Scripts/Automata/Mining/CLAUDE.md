# Mining (2026-10-02)

- `OreScanner`: what a vanilla ore detector marks, read from the voxel storage (ModAPI's ore detector has no markers). Sphere -> 32 m cells (8 voxels of LOD 2); a cell with solid content has its material read with `PreciseOrePositions`; each rare material (`MyVoxelMaterialDefinition.IsRare`, `MinedOre` -> "Ore/X") gives one hit at its voxels' average position. Budgeted: `ScanCellsPerUpdate` cells per update (server config [Mining]). `FindSurface`: where a bore from a deposit outward reaches air.
- `MiningSurveyorBlock` (PB subtype `AutomataLargeMiningSurveyor`): deposits from its own grid's ore detector and from drones' survey messages; 50 m same-ore merge; a scan covering a known deposit that doesn't find it again marks it depleted (remembered 1 h so older scans can't bring it back). Orders `Ore/X:kg` (two listboxes: known ores without an order / orders): one MineOre job per ore in auction at a time, nearest deposit, `min(remaining, job size)` kg counted off when announced and settled by the drone's report (delivered kg); "keep orders constant" never counts. Temporary GPS markers for the owner only. Settings carry a server-written `KnownOres` list (synced) for the terminal.
- Drones: "Survey for ores" (needs a working ore detector) scans while flying and hands the scans to a surveyor in antenna range; mining drones need an ore detector, check the site first (no ore: NoOre, the job ends), bore in terrain clearing mode and collect from 12 m before the deposit (`Drone/DroneControllerBlock.Mining.cs`).
- Overflow limits: finite orders; an LC bids room for ore only with a refinery that takes it (unless "Accept ores/ice") and while neither the ore nor its ingot is at the LC's Max. No LC bid: no award (the ore never goes anywhere unasked).
- Protocol: `TODO/10-handshake.md`.

## Keepalive (2026-10-06)
An awarded MineOre job stays while its drone sends `JOB_KEEPALIVE` (JobStatusReport, Outcome Working, every 60 s); 30 min without one (drone destroyed, dead, out of range): counted back ("no drone keepalive"). The orchestrator's winner announcement now reaches the surveyor too (it never did: jobs stayed unassigned and re-announced).

## Stone (2026-10-06)
"Ignore stone" (on by default, `IgnoreStone`): stone is left out of the known ores, stone orders are refused and old ones make no jobs.

## Stone, LCD (2026-10-06)
- Stone is never a deposit, never listed, never ordered (hard rule; the IgnoreStone setting is kept for a later "mine stone" option, no terminal control).
- Tagged LCDs: sprites. Top 30%: known deposits (left) and active requests (right), 5 rows each, pages every 3 s with "Page: N/M"; below: name, status, deliveries, events (TaggedLcd.UpdateWithHeader, Monospace).

## Retry jobs (2026-10-06)
Terminal button: bumps `RetryRequest` (synced settings); the server, on a change, lifts holds and re-announces every unassigned job, orders planned again. Logistics computers accept Ice without a refinery (O2/H2 generators take it from cargo): MineOre Ice jobs used to get no taker.

## Removed orders (2026-10-06)

Removing an order drops its unassigned jobs and sends `JOB_WITHDRAWN` for the ones already announced, so a running
auction stops (orchestrator drops it, LCs release their promise). Assigned jobs keep going. With several miners on one
job, `DeliveredKg` / `DronesOut` settle the order on the last drone's report.

## Mining workflow (2026-10-11)
- Pooled as before: as many drones as the job's volume needs (MAX_POOLED_DRONES), each from its own start MINER_SPACING apart (angled bores keep the offset across the line: parallel bores). Only the job's ore counts (delivered kg in the report), the surveyor announces the rest again. Everything aboard is unloaded (drill inventories too): refineries take the stone.
- The job's position is general: at the site the drone's own scan picks the ore hit nearest it (`LiveDeposit`), and `OreScanner.NearestOre` (LOD 1, 16 m around the drill point) is the live reading: terrain clearing off and collecting within 7.5 m (straight bore) / 10 m (angled bore) of the nearest ore voxel, or at the bore's end. Then straight on along the bore line (8 m extensions, at most 12); the BackOut is rewritten to go straight back along it (attitude held).
- A bore ends on (`EndBore`): the cargo / load limit (deliver); none of the job's ore for 15 s while collecting (stone crusher mods, stone collection off on the server, the spot ran out, an ice job cutting only iron): back out and, with room left, another bore nearby (`QueueRebore`: a hit of the drone's last scan, else a ring around the last deposit, where `NearestOre` still finds the ore, 10 m from its earlier bores, same angle; at most 4 per job; inserts NavigateToolTo, Mine, BackOut after the BackOut), else deliver. Stuck / too long: deliver.
- Depleted is the detector's call only: a scan (the drone's at the start and end of each bore, the surveyor's own) that no longer finds a known deposit. What was delivered never marks a deposit; a load without the job's ore is delivered as a normal Done (0 kg).
- Natural gravity, drill facing forward / back / left / right (`PlanMineSite`): an angled bore as steep as the pitch (F/B) or roll (L/R) limit allows (2° margin, 85° max, the learnt tilt limit caps it), from the one of 16 directions crossing the least rock (`FindSurface` up to 150 m); site, Mine and BackOut follow that line. Drill facing down / up, or space: the planned straight site. Limits, thrust and cargo that let the drone in and out at that angle are the player's part.
- Ice: per drone, the ice goes to a computer with a working O2/H2 generator, the rest to the best refinery that bid (bid `RefineryTier`: 3 prototech, 2 refinery / industrial, 1 basic "Blast Furnace"; subtype names); one stop when the generator's computer is as good or no other has a refinery. The drone unloads only the job's items when another unload follows (`allOre` off), everything at the last one. LCs without a generator bid ice on their best-refinery network.

## Fixes (2026-10-11, 2)
- Aligned with the site (the final order after the turn in place): drill on in terrain clearing mode, it cuts its way to the surface point; stopped within 4 m of it (approach line's transit or approach): the bore starts there (was approach only: a drone stalled 1.6 m short in transit timed out).
- Stuck boring: back up a hull length (>= 4 m, not past the bore's start) along the bore, drill on, then bore on; 3 times, then deliver (`DispatchBackUp`, `BORE_RETRIES_MAX`).
- While boring: no thrust-gain learning (rock contact isn't thrust), integral clamp 2.0 instead of 0.5 m/s^2 (steady drill / wall pushes).
- Stone stays: delivered to the refineries (no ejection: PAM-style dumping is a simulation-speed cost; admins can add tools for it).

## Collect phase steers to the ore (2026-10-11)
While collecting, every 5th `RunMine` call reads the nearest voxel of the job's ore from the drill point (`NearestOre`, 16 m). More than 2 m off the bore line (and more than `MINE_REACH` 4 m away): the bore's end moves to that voxel + 2 m along the same direction (`DispatchBore` again, at most 12 per bore; attitude and the straight way out unchanged). The 15 s no-ore timer only runs once the drill point is within 4 m of an ore voxel (or no ore voxel is within 16 m, or it has not got closer for 30 s). Before: the bore went on along the line aimed at the deposit's average position; with the nearest voxel several metres off it, 15 s ended the bore with 0 kg (Big mino).

## Silent GPS markers (2026-10-22)
The game plays its GPS sound only for a marker on the HUD when it is added (`MyGpsCollection.AddGpsOnClient`, `PlaySoundOnCreation && ShowOnHud`); `SetShowOnHud` afterwards is silent. `AddGpsSilent`: created hidden + not temporary, added, then shown. Showing clears the game's expiry (`DiscardAt`), so the block removes its own markers:
- "Show deposits as GPS" (on/off switch, no button): `UpdateGps` diffs the wanted markers (`WantedGps`: nearest `MaxGpsMarkers` deposits, keyed by the game's hash = name + position) against `gpsHashes` every refresh: only new ones added, gone ones removed (it used to remove and re-add all every 10 s: a burst of sounds each time). The server turns the switch itself off `GPS_ON_TICKS` (180 s) after it went on (`CheckGpsTimer`: `settings.ShowGps = false` + `SettingsEdited()`, so the players see it), or the player switches it off; either way the markers are removed.
- Description ends with `GpsTag` (" [MS <entity id>]"): the first update after load removes this block's markers it doesn't track (saved now that they don't expire).

