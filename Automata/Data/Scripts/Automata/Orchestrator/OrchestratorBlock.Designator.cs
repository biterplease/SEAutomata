using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Drone;
using Automata.Inventory;
using Automata.Pointer;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;
using Automata.Util.Strings;

namespace Automata.Orchestrator
{
    /// <summary>
    /// Designator orders, the orchestrator's side (the pointing is the target designator block's, TODO/24). A player's order
    /// reaches it as a JobAnnouncement carrying the order fields: from a target designator block over the network
    /// (DESIGNATOR_JOB_ANNOUNCEMENT, read by ReadDesignatorOrders while this orchestrator runs the network), or put straight in by
    /// the hand-held designator (ScoutPointer, AcceptDesignatorJob with origin 0). Either way it becomes a player job
    /// (OrchestratorBlock.PlayerJobs: this orchestrator is the announcer of record, its lifecycle unchanged). The designator
    /// block that sent it is told on DESIGNATOR_ORDER_UPDATE: received, confirmed (with the LCD line), cancelled. A Drone
    /// Controller on the designator's construct never bids on its orders (Auction.NoBidGridId).
    /// </summary>
    public partial class OrchestratorBlock
    {
        private readonly List<Message<JobAnnouncement>> designatorInbox = new List<Message<JobAnnouncement>>();
        private readonly Dictionary<KeyValuePair<long, uint>, int> designatorSeen = new Dictionary<KeyValuePair<long, uint>, int>();
        private readonly List<Message<DesignatorOrderUpdate>> updateOutbox = new List<Message<DesignatorOrderUpdate>>();
        private readonly List<KeyValuePair<long, uint>> designatorSeenGone = new List<KeyValuePair<long, uint>>();
        private const int DESIGNATOR_SEEN_TICKS = 60 * 60;

        /// <summary>A new order id (hand-held designators; designator blocks make their own).</summary>
        public uint NewOrderId()
        {
            return IdGenerator.GenerateId(ref playerJobCounter, entityId);
        }

        // Orders from target designator blocks
        private void ReadDesignatorOrders(int now)
        {
            messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.Orchestrator, antennaReg, Channel.DESIGNATOR_JOB_ANNOUNCEMENT, designatorInbox, 50, true, PayloadType.JobAnnouncement);
            for (int i = 0; i < designatorInbox.Count; i++)
            {
                var msg = designatorInbox[i];
                var ja = msg.Payload;
                if (ja == null || ja.OriginBlockId != msg.SenderId || ja.Tasks == null || ja.OrderId == 0) continue;
                if (!AnchorDirectory.IsAllowed(block.OwnerId, msg.SenderOwnerId)) continue;
                NoteSeen(msg.SenderId, ja.EntityType, now);
                var key = new KeyValuePair<long, uint>(msg.SenderId, ja.JobId);
                if (designatorSeen.ContainsKey(key)) continue;   // read twice
                designatorSeen[key] = now + DESIGNATOR_SEEN_TICKS;
                AcceptDesignatorJob(ja, msg.SenderId, msg.SenderOwnerId);
            }
            designatorInbox.Clear();
            if (designatorSeen.Count > 0)
            {
                designatorSeenGone.Clear();
                foreach (var kv in designatorSeen) if (now > kv.Value) designatorSeenGone.Add(kv.Key);
                for (int i = 0; i < designatorSeenGone.Count; i++) designatorSeen.Remove(designatorSeenGone[i]);
                designatorSeenGone.Clear();
            }
        }

        /// <summary>
        /// A job of a player's order (designator): queued like an announcement of its own (announcer = this orchestrator). The
        /// first job of an order ('ja.OrderId') creates it (marker in the pending colour, the sender told "received"); more jobs
        /// of it (area sweep sectors) join. 'origin': the designator block that sent it (0: a hand-held one). Returns the job id.
        /// </summary>
        public uint AcceptDesignatorJob(JobAnnouncement ja, long origin, long senderOwner)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            PlayerOrder order;
            if (ja.OrderId == 0 || !playerOrders.TryGetValue(ja.OrderId, out order))
            {
                uint orderId = ja.OrderId != 0 ? ja.OrderId : NewOrderId();
                order = new PlayerOrder { OrderId = orderId, Identity = ja.Identity, Kind = ja.Kind, Position = ja.PositionData.ToVector3D(), Name = ja.Marker, OriginBlock = origin };
                playerOrders[orderId] = order;
                ShowGps(order);
                if (origin != 0) SendOrderUpdate(order, 0, DesignatorOrderState.Received, Loc.F(MyStrings.Orchestrator_TakenByOrchestrator, ja.What, Nicknames.Of(Entity)));
            }
            uint jobId = IdGenerator.GenerateId(ref playerJobCounter, entityId);
            // (the received payload is shared with the other readers: the job is a copy of its own)
            var job = new JobAnnouncement
            {
                JobId = jobId, Type = ja.Type, EntityType = ja.EntityType, CreatedTime = DateTime.UtcNow,
                ComponentsInventory = new DiscreteInventory(), PositionData = ja.PositionData, NaturalGravity = ja.NaturalGravity,
                IsInSpace = ja.IsInSpace, IsInAtmosphere = ja.IsInAtmosphere, Tasks = ja.Tasks != null ? new List<Task>(ja.Tasks) : new List<Task>(),
                RequesterEntityId = entityId,
            };
            if (order.Type == JobType.None) order.Type = ja.Type;
            order.Jobs++;
            playerJobs[jobId] = new PlayerJob { JobId = jobId, OrderId = order.OrderId, Announcement = job, RequiredDrone = ja.RequiredDrone,
                                                ExpiresAt = DateTime.UtcNow.AddSeconds(PointerConfig().ConfirmTimeoutSeconds) };
            var key = new KeyValuePair<long, uint>(entityId, jobId);
            knownJobs[key] = int.MaxValue;
            jobQueue.Add(new QueuedJob { Announcement = job, Key = key, SenderOwner = senderOwner, Received = now });
            AddEvent(Loc.F(MyStrings.Orchestrator_OrderedByAPlayer, jobId, ja.Type, ja.What));
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: player job {1} ({2}, order {3}, player {4}, from {5}): {6} task(s), {7}{8}", entityId, jobId, ja.Type, order.OrderId, ja.Identity, origin != 0 ? "designator " + origin : "a hand-held designator", job.Tasks.Count, ja.What, ja.RequiredDrone != 0 ? ", tagged drone " + ja.RequiredDrone : "");
            return jobId;
        }

        // The designator block that sent an order: told how it is going (kept and sent again for a while until it took it)
        private void SendOrderUpdate(PlayerOrder order, uint jobId, DesignatorOrderState state, string text)
        {
            if (order.OriginBlock == 0 || messaging == null) return;
            updateOutbox.Add(new Message<DesignatorOrderUpdate>
            {
                Payload = new DesignatorOrderUpdate { OrderId = order.OrderId, JobId = jobId, State = state, Text = text, OrchestratorEntityId = entityId },
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = block.OwnerId,
                RecipientId = order.OriginBlock,
                RecipientBlockType = MessageQueue.IAIBlockType.TargetDesignator,
                Channel = Channel.DESIGNATOR_ORDER_UPDATE,
            });
            SendOrderUpdates();
        }

        private void SendOrderUpdates()
        {
            for (int i = updateOutbox.Count - 1; i >= 0; i--)
            {
                var msg = updateOutbox[i];
                if (msg.Delivered) { updateOutbox.RemoveAt(i); continue; }
                if (messaging.InFlight(msg)) continue;
                if (DateTime.UtcNow > TimeUtil.TimestampToDateTime(msg.CreatedAt).AddSeconds(30)) { updateOutbox.RemoveAt(i); continue; }
                if (antennaReg == null) continue;
                var error = messaging.BroadcastMessage(antennaReg, msg, false, MessageQueue.IAIBlockType.Orchestrator);
                if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: order {1} {2} to designator {3}: {4}", entityId, msg.Payload.OrderId, msg.Payload.State, msg.RecipientId, error == ErrorCode.None ? "sent" : error.ToString());
            }
        }

        // The grid of the designator block an order came from (0: hand-held): its construct's drones never bid on it
        private long PlayerJobOriginGrid(KeyValuePair<long, uint> key)
        {
            PlayerJob pj;
            PlayerOrder o;
            if (key.Key != entityId || !playerJobs.TryGetValue(key.Value, out pj) || !playerOrders.TryGetValue(pj.OrderId, out o) || o.OriginBlock == 0) return 0;
            IMyEntity e;
            var b = MyAPIGateway.Entities.TryGetEntityById(o.OriginBlock, out e) ? e as IMyCubeBlock : null;
            return b != null && b.CubeGrid != null ? b.CubeGrid.EntityId : 0;
        }

        // A confirmed order for the designator's LCD: one line for one drone, a heading and one indented line per drone for several
        private string DesignatorOrderLine(PlayerOrder order)
        {
            string label;
            switch (order.Type)
            {
                case JobType.DesignatorScoutPoint: label = "SPOT  "; break;
                case JobType.DesignatorScoutArea:  label = "AREA  "; break;
                case JobType.DesignatorEngage:     label = "ENGAGE"; break;
                case JobType.DesignatorEscort:     label = "ESCORT"; break;
                default: return null;
            }
            var drones = new List<string>();
            foreach (var pj in playerJobs.Values)
                if (pj.OrderId == order.OrderId && pj.Assigned && pj.Drone != 0) drones.Add(Nicknames.Of(pj.Drone));
            if (drones.Count == 0) return null;
            bool escort = order.Type == JobType.DesignatorEscort;
            string where = escort ? order.Name : GpsText(order.Position);
            var sb = new System.Text.StringBuilder(label).Append(": ");
            if (drones.Count == 1) sb.Append(escort ? Loc.F(MyStrings.Orchestrator_Arg0EscortingArg1, drones[0], where) : Loc.F(MyStrings.Orchestrator_At, drones[0], where));
            else
            {
                sb.Append(escort ? Loc.F(MyStrings.Orchestrator_EscortingArg0, where) : where);
                for (int i = 0; i < drones.Count; i++) sb.Append("\n  ").Append(drones[i]);
            }
            return sb.ToString();
        }

        // "GPS:x:y:z" (whole metres; short enough for an LCD line)
        private static string GpsText(Vector3D p)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return string.Format(inv, "GPS:{0:F0}:{1:F0}:{2:F0}", p.X, p.Y, p.Z);
        }
    }
}
