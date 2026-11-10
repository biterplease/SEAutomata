using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;
using Automata.Inventory;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Construction
{
    /// <summary>
    /// "Collect trash" (WorkModes.CollectTrash, TODO/15): the floating objects in the area become TrashCollection jobs (a
    /// cluster each: Collect tasks), announced like the block jobs. An object gone is done; a job none of whose objects
    /// is left (and not awarded) is closed. Every item counts (no ignore list, no minimum).
    /// </summary>
    public partial class ConstructionComputerBlock
    {
        private const int MAX_TRASH_JOBS = 2;          // published (unawarded) trash jobs at once
        private const int TRASH_PER_JOB = 12;          // objects per job, at most
        private const double TRASH_CLUSTER = 15;       // m: objects this close to a job's first one join it
        private readonly List<IMyEntity> trashObjects = new List<IMyEntity>();
        private readonly HashSet<long> trashSeen = new HashSet<long>();
        private int lastTrash;

        private void ScanTrash(int now, ref MatrixD area, ref Vector3D half, ref BoundingBoxD box)
        {
            bool on = (settings.WorkModes & WorkModes.CollectTrash) != 0;
            trashObjects.Clear();
            trashSeen.Clear();
            if (on)
            {
                var obb = new MyOrientedBoundingBoxD(area.Translation, half, Quaternion.CreateFromRotationMatrix(area));
                entityBuffer.Clear();
                entityBuffer.AddRange(MyAPIGateway.Entities.GetEntitiesInAABB(ref box));
                for (int i = 0; i < entityBuffer.Count; i++)
                {
                    var e = entityBuffer[i];
                    if (!(e is IMyFloatingObject) || e.Closed || e.MarkedForClose) continue;
                    Vector3D p = e.GetPosition();
                    if (!obb.Contains(ref p)) continue;
                    trashObjects.Add(e);
                    trashSeen.Add(e.EntityId);
                }
                entityBuffer.Clear();
            }
            // Collected / gone: out of their jobs; a published job with nothing left closes (off: every published one)
            bool changed = false;
            for (int j = jobs.Count - 1; j >= 0; j--)
            {
                var job = jobs[j];
                if (job.Type != Orchestrator.JobType.TrashCollection) continue;
                int before = job.Blocks.Count;
                job.Blocks.RemoveWhere(k => !trashSeen.Contains(k.Key));
                if (job.Blocks.Count != before) changed = true;
                if (job.Assigned != 0 || (on && job.Blocks.Count > 0)) continue;
                if (job.Blocks.Count == 0) doneCount++;
                if (Log.DebugEnabled) Log.Debug("CC {0}: trash job {1} closed: {2}", entityId, job.JobId, job.Blocks.Count == 0 ? "nothing left" : "trash collection off");
                jobs.RemoveAt(j);
                changed = true;
            }
            if (changed) RebuildInJobs();
            lastTrash = trashObjects.Count;
            if (!on) return;
            // New jobs: objects in no job yet, nearest the first free one together
            int open = 0;
            for (int j = 0; j < jobs.Count; j++) if (jobs[j].Type == Orchestrator.JobType.TrashCollection && jobs[j].Assigned == 0) open++;
            var group = new List<IMyEntity>();
            for (int i = 0; i < trashObjects.Count && open < MAX_TRASH_JOBS; i++)
            {
                var seed = trashObjects[i];
                if (!TrashFree(seed, now)) continue;
                group.Clear();
                Vector3D at = seed.GetPosition();
                for (int k = i; k < trashObjects.Count && group.Count < TRASH_PER_JOB; k++)
                    if (TrashFree(trashObjects[k], now) && Vector3D.DistanceSquared(trashObjects[k].GetPosition(), at) <= TRASH_CLUSTER * TRASH_CLUSTER)
                        group.Add(trashObjects[k]);
                if (CreateTrashJob(group, now)) open++;
            }
            trashObjects.Clear();
        }

        private bool TrashFree(IMyEntity e, int now)
        {
            var k = TrashKey(e.EntityId);
            return !inJobs.Contains(k) && !CoolingDown(k, now);
        }

        private static KeyValuePair<long, Vector3I> TrashKey(long id)
        {
            return new KeyValuePair<long, Vector3I>(id, Vector3I.Zero);
        }

        private bool CreateTrashJob(List<IMyEntity> group, int now)
        {
            if (group.Count == 0) return false;
            var job = computer.NewJob(Orchestrator.JobType.TrashCollection);
            var items = new DiscreteInventory();
            var names = new Dictionary<string, int>();
            Vector3D centre = Vector3D.Zero;
            float interference;
            for (int i = 0; i < group.Count; i++)
            {
                var e = group[i];
                Vector3D p = e.GetPosition();
                centre += p;
                float g = MyAPIGateway.Physics.CalculateNaturalGravityAt(p, out interference).Length();
                job.Tasks.Add(Orchestrator.Task.Collect(e.EntityId, p, g));
                var ob = e.GetObjectBuilder() as MyObjectBuilder_FloatingObject;
                if (ob != null && ob.Item != null && ob.Item.PhysicalContent != null)
                {
                    var id = ob.Item.PhysicalContent.GetId();
                    int amount = Math.Max(1, (int)Math.Ceiling((double)ob.Item.Amount));
                    items.AddItem(ItemKeys.Of(id), amount);
                    string n = AutomataSession.ItemDisplayName(ItemKeys.Of(id).String);
                    int c;
                    names.TryGetValue(n, out c);
                    names[n] = c + 1;
                }
            }
            centre /= group.Count;
            job.TotalInventory = items;
            job.BlockCount = (ushort)group.Count;
            float gc = MyAPIGateway.Physics.CalculateNaturalGravityAt(centre, out interference).Length();
            var sb = new StringBuilder();
            foreach (var kv in names) { if (sb.Length > 0) sb.Append('\n'); sb.Append("  ").Append(kv.Key).Append(": ").Append(kv.Value); }
            var open = new OpenJob { JobId = job.JobId, Type = job.JobType, Category = WorkCategory.Grind, Opened = now, Summary = sb.ToString() };
            for (int i = 0; i < group.Count; i++)
            {
                var k = TrashKey(group[i].EntityId);
                open.Blocks.Add(k);
                inJobs.Add(k);
            }
            var ja = JobAnnouncement.FromJob(job, AutomataEntityType.ConstructionComputerBlock, block.CubeGrid.IsStatic);
            ja.ComponentsInventory = items;
            ja.PositionData = Util.Vector3DData.FromVector3D(centre);
            ja.CreatedTime = DateTime.UtcNow;
            ja.RequesterEntityId = entityId;
            ja.NaturalGravity = gc;
            ja.IsInSpace = gc < JobAnnouncement.SPACE_GRAVITY;
            ja.IsInAtmosphere = gc >= JobAnnouncement.SPACE_GRAVITY;
            open.Message = AnnouncementMessage(ja);
            jobs.Add(open);
            if (Log.DebugEnabled) Log.Debug("CC {0}: trash job {1}: {2} object(s) around {3}: {4}", entityId, job.JobId, group.Count, centre, Describe(items));
            return true;
        }
    }
}
