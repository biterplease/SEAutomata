using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using Automata.Drone;
using Automata.Inventory;
using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;
using Automata.VirtualNetwork;

namespace Automata.Logistics
{
    /// <summary>
    /// Farming (TODO/15): the farm plots on this computer's grids (any block with the game's farm plot logic, not algae /
    /// oxygen farms) are checked every FARM_CHECK_TICKS. Plots with a fully grown crop it doesn't ignore, and dead plants
    /// (cleared), become one FarmCrops job (one grid at a time, the one with the most): Harvest tasks, the crops estimated,
    /// unloaded at this computer (its own dock: the orchestrator sends the drone here first). One job at a time; when its
    /// drone reports, the next check makes the next one. The orchestrator tells the player when a farm is harvested.
    /// </summary>
    public partial class LogisticsComputerBlock
    {
        private const int FARM_CHECK_TICKS = 30 * 60;
        private const int FARM_PLOTS_PER_JOB = 24;
        private OpenJob farmJob;
        private int lastFarmCheck = int.MinValue / 2;
        private int farmDue;   // plots due at the last check (header)

        private void CheckFarm(int now)
        {
            if (!settings.HarvestCrops)
            {
                if (farmJob != null && farmJob.Assigned == 0) farmJob = null;   // an awarded one finishes
                farmDue = 0;
                return;
            }
            if (farmJob != null && farmJob.Finished) { farmJob = null; lastFarmCheck = now; }   // (the next check in FARM_CHECK_TICKS: plots it couldn't reach aren't sent again at once)
            if (farmJob != null) { RepeatFarmAnnouncement(farmJob, now); return; }
            if (now - lastFarmCheck < FARM_CHECK_TICKS) return;
            lastFarmCheck = now;
            // Due plots by grid
            var byGrid = new Dictionary<long, List<IMyCubeBlock>>();
            farmDue = 0;
            for (int g = 0; g < grids.Count; g++)
            {
                var grid = grids[g];
                if (grid == null || grid.Closed) continue;
                foreach (var b in grid.GetFatBlocks<IMyTerminalBlock>())
                {
                    var farm = DroneControllerBlock.FarmLogic(b);
                    if (farm == null || !b.IsFunctional || !Due(farm)) continue;
                    List<IMyCubeBlock> list;
                    if (!byGrid.TryGetValue(grid.EntityId, out list)) byGrid[grid.EntityId] = list = new List<IMyCubeBlock>();
                    list.Add(b);
                    farmDue++;
                }
            }
            List<IMyCubeBlock> plots = null;
            foreach (var kv in byGrid) if (plots == null || kv.Value.Count > plots.Count) plots = kv.Value;
            if (plots == null) return;
            if (plots.Count > FARM_PLOTS_PER_JOB) plots.RemoveRange(FARM_PLOTS_PER_JOB, plots.Count - FARM_PLOTS_PER_JOB);
            // Nearest neighbour order from the first: one sweep along the rows
            var ordered = new List<IMyCubeBlock> { plots[0] };
            plots.RemoveAt(0);
            while (plots.Count > 0)
            {
                Vector3D last = ordered[ordered.Count - 1].GetPosition();
                int best = 0;
                for (int i = 1; i < plots.Count; i++)
                    if (Vector3D.DistanceSquared(plots[i].GetPosition(), last) < Vector3D.DistanceSquared(plots[best].GetPosition(), last)) best = i;
                ordered.Add(plots[best]);
                plots.RemoveAt(best);
            }
            var items = new DiscreteInventory();
            var tasks = new List<Orchestrator.Task>();
            Vector3D centre = Vector3D.Zero;
            float interference;
            for (int i = 0; i < ordered.Count; i++)
            {
                var p = ordered[i];
                var farm = DroneControllerBlock.FarmLogic(p);
                Vector3D c = p.PositionComp.WorldAABB.Center;
                centre += c;
                float gr = MyAPIGateway.Physics.CalculateNaturalGravityAt(c, out interference).Length();
                tasks.Add(Orchestrator.Task.Harvest(p.EntityId, c, p.WorldMatrix.Up, gr));
                if (farm != null && farm.IsAlive && farm.IsPlantFullyGrown && farm.OutputItemAmount > 0)
                    items.AddItem(ItemKeys.Of(farm.OutputItem), farm.OutputItemAmount);
            }
            centre /= ordered.Count;
            farmJob = new OpenJob
            {
                JobId = IdGenerator.GenerateId(ref jobCounter, entityId),
                Type = Orchestrator.JobType.FarmCrops,
                Items = items,
                Opened = now,
                LastAnnounced = int.MinValue / 2,
                Tasks = tasks,
                Place = ordered[0].CubeGrid.DisplayName,
                Position = centre,
            };
            if (Log.DebugEnabled) Log.Debug("LC {0}: farm job {1}: {2} plot(s) on '{3}' ({4} due in all): {5}", entityId, farmJob.JobId, ordered.Count, farmJob.Place, farmDue, DescribeItems(items));
            RepeatFarmAnnouncement(farmJob, now);
        }

        // A plot to visit: a fully grown crop not ignored, or a dead plant (cleared whatever it was)
        private bool Due(IMyFarmPlotLogic farm)
        {
            if (!farm.IsPlantPlanted || !farm.IsHarvestable) return false;
            if (!farm.IsAlive) return true;
            return farm.IsPlantFullyGrown && !DroneControllerBlock.CropIgnored(settings.IgnoredCrops, ItemKeys.Of(farm.OutputItem).String);
        }

        // As RepeatAnnouncement, with the job's harvest tasks: at the farm, unloaded at this computer's main network
        private void RepeatFarmAnnouncement(OpenJob job, int now)
        {
            if (job.HeldUntil != 0)
            {
                if (now < job.HeldUntil) return;
                job.HeldUntil = 0;
                job.LastAnnounced = int.MinValue / 2;
            }
            if (job.Assigned != 0)
            {
                if (now - job.Assigned <= ASSIGNED_TTL_TICKS) return;
                job.Finished = true;   // awarded long ago, never done: checked again
                return;
            }
            if (job.Sent != null)
            {
                if (job.Sent.Delivered) { job.Announced = true; job.Sent = null; job.LastResult = Loc.T(MyStrings.Logistics_Announced); }
                else if (now - job.LastAnnounced >= messaging.LifetimeTicks) { job.Announced = false; job.Sent = null; job.LastResult = Loc.T(MyStrings.Logistics_NoOrchestratorInRange); }
                else return;
            }
            if (now - job.LastAnnounced < (job.Announced ? RESPONSE_TIMEOUT_TICKS : RETRY_TICKS)) return;
            job.LastAnnounced = now;
            var dock = primary != null ? FreeDock(primary) : null;
            if (dock == null) { job.LastResult = Loc.T(MyStrings.Logistics_NoFreeConnector); return; }
            float interference;
            float g = MyAPIGateway.Physics.CalculateNaturalGravityAt(job.Position, out interference).Length();
            var msg = new Message<JobAnnouncement>
            {
                Payload = new JobAnnouncement
                {
                    JobId = job.JobId,
                    Type = job.Type,
                    EntityType = AutomataEntityType.LogisticsComputerBlock,
                    CreatedTime = DateTime.UtcNow,
                    ComponentsInventory = job.Items,
                    PositionData = Vector3DData.FromVector3D(job.Position),
                    NaturalGravity = g,
                    IsInSpace = g < JobAnnouncement.SPACE_GRAVITY,
                    IsInAtmosphere = g >= JobAnnouncement.SPACE_GRAVITY,
                    IsStaticGrid = block.CubeGrid.IsStatic,
                    RequesterEntityId = entityId,
                    Dock = dock,
                    DockSite = DockFit.SiteOf(primary, dock.PositionData.ToVector3D()),
                    Tasks = new List<Orchestrator.Task>(job.Tasks),
                    BlockCount = (ushort)job.Tasks.Count,
                    Place = job.Place,
                },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.LOGISTIC_PUSH,
            };
            var error = messaging.BroadcastMessage(antennaReg, msg, false, MessageQueue.IAIBlockType.LogisticsComputer);
            job.Announced = false;
            job.Sent = error == ErrorCode.None ? msg : null;
            if (error != ErrorCode.None) job.LastResult = error.ToString();
            if (Log.DebugEnabled) Log.Debug("LC {0}: farm job {1} ({2} plot(s) on '{3}') announced: {4}", entityId, job.JobId, job.Tasks.Count, job.Place, error);
        }

        // A trash collection's construction computer on this computer's grids: its bids rank first
        private bool OnOwnGrids(long blockId)
        {
            IMyEntity e;
            var b = blockId != 0 && MyAPIGateway.Entities.TryGetEntityById(blockId, out e) ? e as IMyCubeBlock : null;
            return b != null && grids.Contains(b.CubeGrid);
        }

        #region Terminal: Farming section (checkbox; crop ignore lists, as the drone's)
        private static readonly List<KeyValuePair<string, string>> farmKnown = new List<KeyValuePair<string, string>>();
        private static readonly List<string> cropKnownSel = new List<string>(), cropIgnoredSel = new List<string>();

        public bool Terminal_Farming
        {
            get { return settings.Farming; }
            set { if (settings.Farming == value) return; settings.Farming = value; SettingsEdited(); }
        }

        public bool Terminal_HarvestCrops
        {
            get { return settings.HarvestCrops; }
            set { if (settings.HarvestCrops == value) return; settings.HarvestCrops = value; lastFarmCheck = int.MinValue / 2; SettingsEdited(); }
        }

        private List<string> IgnoredCrops
        {
            get { if (settings.IgnoredCrops == null) settings.IgnoredCrops = new List<string>(); return settings.IgnoredCrops; }
        }

        public void Terminal_CropKnownContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var ignored = IgnoredCrops;
            cropKnownSel.RemoveAll(k => ignored.Contains(k));
            AutomataSession.ListItemsOfType(DroneControllerBlock.CROP_TYPE_ID, farmKnown);
            for (int i = 0; i < farmKnown.Count; i++)
            {
                if (ignored.Contains(farmKnown[i].Key)) continue;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(farmKnown[i].Value), MyStringId.GetOrCompute(farmKnown[i].Key), farmKnown[i].Key);
                items.Add(item);
                if (cropKnownSel.Contains(farmKnown[i].Key)) selected.Add(item);
            }
            farmKnown.Clear();
        }

        public void Terminal_CropIgnoredContent(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var ignored = IgnoredCrops;
            cropIgnoredSel.RemoveAll(k => !ignored.Contains(k));
            for (int i = 0; i < ignored.Count; i++)
            {
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(AutomataSession.ItemDisplayName(ignored[i])), MyStringId.GetOrCompute(ignored[i]), ignored[i]);
                items.Add(item);
                if (cropIgnoredSel.Contains(ignored[i])) selected.Add(item);
            }
        }

        public static void Terminal_CropSelect(bool ignoredList, List<MyTerminalControlListBoxItem> selected)
        {
            var into = ignoredList ? cropIgnoredSel : cropKnownSel;
            into.Clear();
            if (selected == null) return;
            for (int i = 0; i < selected.Count; i++) { var k = selected[i].UserData as string; if (k != null) into.Add(k); }
        }

        public void Terminal_CropIgnore(bool add)
        {
            var list = new List<string>(IgnoredCrops);
            var sel = add ? cropKnownSel : cropIgnoredSel;
            for (int i = 0; i < sel.Count; i++)
            {
                if (add) { if (!list.Contains(sel[i])) list.Add(sel[i]); }
                else list.Remove(sel[i]);
            }
            settings.IgnoredCrops = list;
            SettingsEdited();
        }
        #endregion
    }
}
