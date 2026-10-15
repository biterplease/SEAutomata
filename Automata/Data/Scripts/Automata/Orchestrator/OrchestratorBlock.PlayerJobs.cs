using System;
using System.Collections.Generic;

using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Inventory;
using Automata.Pointer;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;
using VRage.Utils;

namespace Automata.Orchestrator
{
    /// <summary>
    /// Jobs a player ordered with the industrial target designator (TODO/11). No queueing for later: an order is
    /// confirmed or cancelled within seconds.
    ///  - Ordered: a GPS marker for the player in PendingGpsColor (yellow), the job queued and auctioned at once.
    ///  - Confirmed: every job of the order has a drone (auction won) within ScoutPointer.ConfirmTimeoutSeconds
    ///    (server setting, 10 s): the marker turns ConfirmedGpsColor (green), the player is told (chat, sound).
    ///  - Not confirmed in time (no bid, no workable bid, still queued): the job is cancelled (queue / auction removed);
    ///    an order left without jobs loses its marker, the player is told (chat, error sound).
    ///  - Only drones with the job's behaviour profile may take them (<see cref="RequiredProfile"/>).
    ///  - The marker stays until all the order's jobs have ended: done / aborted (recall) reported by the drone, or no
    ///    report within PLAYER_JOB_MEMORY_TICKS (2 h) of the award (drone lost). An area sweep is one order, one marker,
    ///    one job per sector; its marker is shown from the first hit (BeginPlayerOrder).
    /// Saved with the orchestrator's state; the markers (temporary, not saved by the game) are re-created on load.
    /// </summary>
    public partial class OrchestratorBlock
    {
        #region UI strings
        private static readonly MyStringId StrOreScout = MyStringId.GetOrCompute("ore scout");
        private static readonly MyStringId StrOreDetectorDrone = MyStringId.GetOrCompute("drone with an ore detector");
        private static readonly MyStringId StrCombatDrone = MyStringId.GetOrCompute("combat drone");
        private static readonly MyStringId StrOreHauler = MyStringId.GetOrCompute("ore hauler");
        private static readonly MyStringId StrMiningDrone = MyStringId.GetOrCompute("mining drone");
        private static readonly MyStringId StrJobOrderedByAPlayer = MyStringId.GetOrCompute("{1}: ordered by a player\n  {2}");
        private static readonly MyStringId StrTagged = MyStringId.GetOrCompute("tagged {0} ({1})");
        private static readonly MyStringId StrJobCancelledNotConfirmedIn = MyStringId.GetOrCompute("{1}: cancelled, not confirmed in {2} s");
        private static readonly MyStringId StrNotConfirmedInTime = MyStringId.GetOrCompute("not confirmed in time");
        private static readonly MyStringId StrScoutSpotJobAcceptedOre = MyStringId.GetOrCompute("Scout spot job accepted: {0} ore scouts on the way.");
        private static readonly MyStringId StrScoutSpotJobAcceptedAn = MyStringId.GetOrCompute("Scout spot job accepted: an ore scout is on the way.");
        private static readonly MyStringId StrScoutAreaJobAcceptedOre = MyStringId.GetOrCompute("Scout area job accepted: {0} ore scout(s) on the way{1}.");
        private static readonly MyStringId StrSectorsCancelled = MyStringId.GetOrCompute(", {0} sector(s) cancelled");
        private static readonly MyStringId StrCombatEngageAcceptedCombatDrones = MyStringId.GetOrCompute("Combat engage accepted: {0} combat drones on the way to {1}.");
        private static readonly MyStringId StrCombatEngageAcceptedACombat = MyStringId.GetOrCompute("Combat engage accepted: a combat drone is on the way to {0}.");
        private static readonly MyStringId StrCombatEscortAcceptedCombatDrones = MyStringId.GetOrCompute("Combat escort accepted: {0} combat drones on the way to escort {1}.");
        private static readonly MyStringId StrCombatEscortAcceptedACombat = MyStringId.GetOrCompute("Combat escort accepted: a combat drone is on the way to escort {0}.");
        private static readonly MyStringId StrAutomataDesignatorOrderOrchestrator = MyStringId.GetOrCompute("Automata: {0} designator order {1} (orchestrator '{2}')");
        #endregion

        private sealed class PlayerJob
        {
            public uint JobId, OrderId;
            public JobAnnouncement Announcement;
            public DateTime ExpiresAt;     // not assigned by then: cancelled
            public bool Assigned;
            public long RequiredDrone;     // a tagged drone's order: only it may take the job (0: any of the profile)
            public long Drone;             // the drone that won it (designator LCDs)
        }

        private sealed class PlayerOrder
        {
            public uint OrderId;
            public long Identity;
            public PointerKind Kind;
            public JobType Type;           // of its jobs (an order holds one kind)
            public Vector3D Position;
            public string Name;
            public IMyGps Gps;
            public int Jobs;               // jobs not ended
            public int Assigned;           // of those, with a drone
            public int Cancelled;          // jobs cancelled unconfirmed
            public bool Confirmed;
        }

        private readonly Dictionary<uint, PlayerJob> playerJobs = new Dictionary<uint, PlayerJob>();
        private readonly Dictionary<uint, PlayerOrder> playerOrders = new Dictionary<uint, PlayerOrder>();
        private readonly List<uint> playerJobsEnded = new List<uint>();
        private readonly List<PlayerOrder> playerOrdersTouched = new List<PlayerOrder>();

        /// <summary>
        /// The behaviour profile a job type requires of its drone (the player designates the drones for it). None:
        /// any capable drone. Area sweeps: ore scouts; spot checks: any drone with an ore detector (CanScoutOre, checked
        /// with the capabilities; ore scouts still rank first: PreferredProfile); combat jobs (engage / escort): combat drones.
        /// </summary>
        private static Drone.BehaviourProfile RequiredProfile(JobType type)
        {
            switch (type)
            {
                case JobType.DesignatorScoutArea: return Drone.BehaviourProfile.IsOreScout;
                case JobType.DesignatorEngage:
                case JobType.DesignatorEscort:    return Drone.BehaviourProfile.IsCombatDrone;
                default:                          return Drone.BehaviourProfile.None;
            }
        }

        // Who may take a designator job, for the log / feedback ("no {0} bid")
        private static string DesignatorTakerName(JobType type)
        {
            return type == JobType.DesignatorScoutPoint ? Loc.T(StrOreDetectorDrone) : ProfileName(RequiredProfile(type));
        }

        private static string ProfileName(Drone.BehaviourProfile p)
        {
            return p == Drone.BehaviourProfile.IsOreScout ? Loc.T(StrOreScout) : p == Drone.BehaviourProfile.IsCombatDrone ? Loc.T(StrCombatDrone)
                 : p == Drone.BehaviourProfile.IsOreHauler ? Loc.T(StrOreHauler) : p == Drone.BehaviourProfile.IsMiningDrone ? Loc.T(StrMiningDrone) : p.ToString();
        }

        /// <summary>A new order: its marker (pending color) shows at once; its jobs follow (SubmitPlayerJob).</summary>
        public uint BeginPlayerOrder(long identity, PointerKind kind, Vector3D position, string marker)
        {
            uint orderId = IdGenerator.GenerateId(ref playerJobCounter, entityId);
            var order = new PlayerOrder { OrderId = orderId, Identity = identity, Kind = kind, Position = position, Name = marker };
            playerOrders[orderId] = order;
            ShowGps(order);
            return orderId;
        }

        /// <summary>
        /// A job a player ordered (designator): queued like an announcement of its own (announcer = this
        /// orchestrator). orderId 0: a new order (BeginPlayerOrder, marker named 'marker'); otherwise one more job of
        /// that order (area sweep sectors). Returns the job id.
        /// </summary>
        public uint SubmitPlayerJob(JobType type, List<Task> tasks, Vector3D site, string what, long identity, PointerKind kind, string marker,
                                    ref uint orderId, long requiredDrone = 0)
        {
            int now = MyAPIGateway.Session.GameplayFrameCounter;
            PlayerOrder order;
            if (orderId == 0 || !playerOrders.TryGetValue(orderId, out order))
            {
                orderId = BeginPlayerOrder(identity, kind, site, marker);
                order = playerOrders[orderId];
            }
            uint jobId = IdGenerator.GenerateId(ref playerJobCounter, entityId);
            float g = ScoutPlanner.Gravity(site);
            var ja = new JobAnnouncement
            {
                JobId = jobId, Type = type, EntityType = AutomataEntityType.OrchestratorBlock, CreatedTime = DateTime.UtcNow,
                ComponentsInventory = new DiscreteInventory(), PositionData = Vector3DData.FromVector3D(site), NaturalGravity = g,
                IsInSpace = g < JobAnnouncement.SPACE_GRAVITY, IsInAtmosphere = g >= JobAnnouncement.SPACE_GRAVITY,
                Tasks = tasks, RequesterEntityId = entityId,
            };
            if (order.Type == JobType.None) order.Type = type;
            order.Jobs++;
            playerJobs[jobId] = new PlayerJob { JobId = jobId, OrderId = orderId, Announcement = ja, RequiredDrone = requiredDrone,
                                                ExpiresAt = DateTime.UtcNow.AddSeconds(PointerConfig().ConfirmTimeoutSeconds) };
            var key = new KeyValuePair<long, uint>(entityId, jobId);
            knownJobs[key] = int.MaxValue;
            jobQueue.Add(new QueuedJob { Announcement = ja, Key = key, SenderOwner = block.OwnerId, Received = now });
            AddEvent(Loc.F(StrJobOrderedByAPlayer, jobId, type, what));
            Log.Info("Orchestrator {0}: player job {1} ({2}, order {3}, player {4}): {5} task(s), {6}{7}", entityId, jobId, type, orderId, identity,
                     tasks.Count, what, requiredDrone != 0 ? ", tagged drone " + requiredDrone : "");
            return jobId;
        }

        private static Config.ServerConfig.ScoutPointerConfig PointerConfig()
        {
            var config = AutomataSession.GetConfig();
            return config != null ? config.ScoutPointer : new Config.ServerConfig.ScoutPointerConfig();
        }

        // The tagged drone a player job is for (0: any drone of the profile; not a player job)
        private long PlayerJobDrone(KeyValuePair<long, uint> key)
        {
            PlayerJob pj;
            return key.Key == entityId && playerJobs.TryGetValue(key.Value, out pj) ? pj.RequiredDrone : 0;
        }

        /// <summary>"Use broadcast controller": a drone tagged with a designator ("tagged 'name' (engage)").</summary>
        public void BroadcastTagged(string droneName, string mode)
        {
            if (settings == null || !settings.UseBroadcastController) return;
            string via;
            bool sent = BroadcastControllerSender.Send(block, Loc.F(StrTagged, droneName, mode), out via);
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: tagged {1} ({2}), broadcast {3}: {4}", entityId, droneName, mode, sent ? "sent via" : "not sent", via);
        }

        private bool IsPlayerJob(KeyValuePair<long, uint> key)
        {
            return key.Key == entityId && playerJobs.ContainsKey(key.Value);
        }

        /// <summary>
        /// An auction of a player job closing without a winner: no second try, the job is cancelled at the next
        /// ExpirePlayerJobs (true: the caller closes the auction). False: not a player job.
        /// </summary>
        private bool RequeuePlayerJob(OpenAuction open, int now, string why)
        {
            PlayerJob pj;
            if (open.JobKey.Key != entityId || !playerJobs.TryGetValue(open.JobKey.Value, out pj)) return false;
            pj.ExpiresAt = DateTime.UtcNow;
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: player job {1} ({2}): no taker ({3})", entityId, pj.JobId, pj.Announcement.Type, why);
            return true;
        }

        /// <summary>A player job out of the queue: false once past its confirmation time (ExpirePlayerJobs cancels it).</summary>
        private bool PlayerJobStillWanted(KeyValuePair<long, uint> key)
        {
            PlayerJob pj;
            if (key.Key != entityId || !playerJobs.TryGetValue(key.Value, out pj)) return true;
            return DateTime.UtcNow < pj.ExpiresAt;
        }

        // An awarded player job with no report this long: the drone is taken as lost and the marker goes (sweeps are long)
        #region Timing
        private const int PLAYER_JOB_MEMORY_TICKS = 2 * 60 * 3600;
        #endregion

        private void PlayerJobAssigned(KeyValuePair<long, uint> key, int now, long droneId)
        {
            PlayerJob pj;
            if (key.Key != entityId || !playerJobs.TryGetValue(key.Value, out pj) || pj.Assigned) return;
            pj.Assigned = true;
            pj.Drone = droneId;
            knownJobs[key] = now + PLAYER_JOB_MEMORY_TICKS;
            PlayerOrder order;
            if (playerOrders.TryGetValue(pj.OrderId, out order))
            {
                order.Assigned++;
                CheckOrder(order);
            }
        }

        /// <summary>Every step: player jobs not confirmed in time are cancelled (queue / auction removed).</summary>
        private void ExpirePlayerJobs(int now)
        {
            if (playerJobs.Count == 0) return;
            DateTime utc = DateTime.UtcNow;
            playerJobsEnded.Clear();
            foreach (var pj in playerJobs.Values)
                if (!pj.Assigned && utc >= pj.ExpiresAt) playerJobsEnded.Add(pj.JobId);
            if (playerJobsEnded.Count == 0) return;
            playerOrdersTouched.Clear();
            for (int i = 0; i < playerJobsEnded.Count; i++)
            {
                uint jobId = playerJobsEnded[i];
                var key = new KeyValuePair<long, uint>(entityId, jobId);
                for (int q = jobQueue.Count - 1; q >= 0; q--)
                    if (jobQueue[q].Key.Equals(key)) jobQueue.RemoveAt(q);
                foreach (var open in auctions.Values)
                    if (open.JobKey.Equals(key)) closing.Add(open.Auction.AuctionId);
                for (int c = 0; c < closing.Count; c++) { auctions.Remove(closing[c]); _auctionJobs.Remove(closing[c]); }
                closing.Clear();
                knownJobs.Remove(key);
                droppedCount++;
                PlayerJob pj = playerJobs[jobId];
                PlayerOrder order;
                if (playerOrders.TryGetValue(pj.OrderId, out order))
                {
                    order.Cancelled++;
                    if (!playerOrdersTouched.Contains(order)) playerOrdersTouched.Add(order);
                }
                AddEvent(Loc.F(StrJobCancelledNotConfirmedIn, jobId, pj.Announcement.Type, PointerConfig().ConfirmTimeoutSeconds));
                EndPlayerJob(jobId, Loc.T(StrNotConfirmedInTime));
            }
            playerJobsEnded.Clear();
            for (int i = 0; i < playerOrdersTouched.Count; i++)
            {
                var order = playerOrdersTouched[i];
                if (order.Jobs <= 0)
                {
                    ScoutPointer.Feedback(order.Identity, DesignatorSignal.Error);
                }
                else CheckOrder(order);
            }
            playerOrdersTouched.Clear();
        }

        // All the order's (remaining) jobs have a drone: confirmed, the marker turns green, the player told
        private void CheckOrder(PlayerOrder order)
        {
            if (order.Confirmed || order.Jobs <= 0 || order.Assigned < order.Jobs) return;
            order.Confirmed = true;
            SetGpsColor(order, PointerConfig().ConfirmedGpsColor);
            // Spot check / engage: green; area sweep / escort: blue
            ScoutPointer.Feedback(order.Identity, order.Type == JobType.DesignatorScoutArea || order.Type == JobType.DesignatorEscort
                                                  ? DesignatorSignal.ConfirmedBlue : DesignatorSignal.ConfirmedGreen);
            BroadcastAccepted(order);
            LogDesignatorOrder(order);
            Log.Info("Orchestrator {0}: player order {1} confirmed ({2} job(s), {3} cancelled)", entityId, order.OrderId, order.Assigned, order.Cancelled);
        }

        // "Use broadcast controller": the accepted order as a chat line through a Broadcast Controller on this construct
        private void BroadcastAccepted(PlayerOrder order)
        {
            if (!settings.UseBroadcastController) return;
            string text;
            switch (order.Type)
            {
                case JobType.DesignatorScoutPoint:
                    text = order.Assigned > 1 ? Loc.F(StrScoutSpotJobAcceptedOre, order.Assigned)
                                              : Loc.T(StrScoutSpotJobAcceptedAn);
                    break;
                case JobType.DesignatorScoutArea:
                    text = Loc.F(StrScoutAreaJobAcceptedOre, order.Assigned,
                                         order.Cancelled > 0 ? Loc.F(StrSectorsCancelled, order.Cancelled) : "");
                    break;
                case JobType.DesignatorEngage:
                    text = order.Assigned > 1 ? Loc.F(StrCombatEngageAcceptedCombatDrones, order.Assigned, order.Name)
                                              : Loc.F(StrCombatEngageAcceptedACombat, order.Name);
                    break;
                case JobType.DesignatorEscort:
                    text = order.Assigned > 1 ? Loc.F(StrCombatEscortAcceptedCombatDrones, order.Assigned, order.Name)
                                              : Loc.F(StrCombatEscortAcceptedACombat, order.Name);
                    break;
                default:
                    return;
            }
            string via;
            bool sent = BroadcastControllerSender.Send(block, text, out via);
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: order {1} accepted, broadcast {2}: {3}", entityId, order.OrderId, sent ? "sent via" : "not sent", via);
        }

        private readonly List<uint> recallJobIds = new List<uint>();

        /// <summary>
        /// Recall aimed at a marker: the order whose GPS marker (a confirmed, non-combat order) is nearest to 'hit' within
        /// 'radius'. Its jobs end: the ones not yet taken are cancelled (queue / auction), the drones working the others
        /// are recalled (their "recalled" report ends the job and, with the last one, the marker). False: no marker there.
        /// </summary>
        public bool RecallOrderAt(Vector3D hit, double radius, string by, out int drones, out int cancelled)
        {
            drones = cancelled = 0;
            PlayerOrder best = null;
            double bestD = radius * radius;
            foreach (var o in playerOrders.Values)
            {
                if (!o.Confirmed || o.Kind == PointerKind.Combat) continue;
                double d = Vector3D.DistanceSquared(o.Position, hit);
                if (d <= bestD) { bestD = d; best = o; }
            }
            if (best == null) return false;
            recallJobIds.Clear();
            foreach (var pj in playerJobs.Values) if (pj.OrderId == best.OrderId) recallJobIds.Add(pj.JobId);
            var taken = new List<uint>();
            for (int i = 0; i < recallJobIds.Count; i++)
            {
                uint jobId = recallJobIds[i];
                var pj = playerJobs[jobId];
                if (pj.Assigned) { taken.Add(jobId); continue; }
                var key = new KeyValuePair<long, uint>(entityId, jobId);
                for (int q = jobQueue.Count - 1; q >= 0; q--)
                    if (jobQueue[q].Key.Equals(key)) jobQueue.RemoveAt(q);
                foreach (var open in auctions.Values)
                    if (open.JobKey.Equals(key)) closing.Add(open.Auction.AuctionId);
                for (int c = 0; c < closing.Count; c++) { auctions.Remove(closing[c]); _auctionJobs.Remove(closing[c]); }
                closing.Clear();
                knownJobs.Remove(key);
                droppedCount++;
                cancelled++;
                EndPlayerJob(jobId, by);
            }
            recallJobIds.Clear();
            if (taken.Count > 0) drones = Drone.DroneControllerBlock.RecallJobs(entityId, taken, by);
            if (Log.DebugEnabled) Log.Debug("Orchestrator {0}: order {1} recalled at its marker ({2}): {3} drone(s) recalled, {4} job(s) cancelled", entityId, best.OrderId, by,
                      drones, cancelled);
            return true;
        }

        /// <summary>The job is over (reported, cancelled, forgotten): the order's marker goes with its last job.</summary>
        private void EndPlayerJob(uint jobId, string reason)
        {
            PlayerJob pj;
            if (!playerJobs.TryGetValue(jobId, out pj)) return;
            playerJobs.Remove(jobId);
            PlayerOrder order;
            if (!playerOrders.TryGetValue(pj.OrderId, out order)) return;
            Log.Info("Orchestrator {0}: player job {1} (order {2}) ended: {3}", entityId, jobId, pj.OrderId, reason);
            if (pj.Assigned) order.Assigned--;
            if (--order.Jobs > 0) return;
            HideGps(order);
            playerOrders.Remove(order.OrderId);
        }

        private void ShowGps(PlayerOrder order)
        {
            // Combat orders: no marker (the target moves; feedback is the sound and the broadcast)
            if (order.Identity == 0 || order.Kind == PointerKind.Combat) return;
            try
            {
                var cfg = PointerConfig();
                var gps = MyAPIGateway.Session.GPS.Create(order.Name, Loc.F(StrAutomataDesignatorOrderOrchestrator,
                                                          order.Kind, order.OrderId, block != null ? block.CustomName : entityId.ToString()),
                                                          order.Position, true, true);
                gps.GPSColor = order.Confirmed ? cfg.ConfirmedGpsColor : cfg.PendingGpsColor;
                MyAPIGateway.Session.GPS.AddGps(order.Identity, gps);
                order.Gps = gps;
            }
            catch (Exception ex)
            {
                Log.Error("Orchestrator {0}: GPS for order {1}: {2}", entityId, order.OrderId, ex.Message);
            }
        }

        private void SetGpsColor(PlayerOrder order, Color color)
        {
            if (order.Gps == null) return;
            try
            {
                order.Gps.GPSColor = color;
                MyAPIGateway.Session.GPS.ModifyGps(order.Identity, order.Gps);
            }
            catch (Exception ex) { Log.Error("Orchestrator {0}: GPS color for order {1}: {2}", entityId, order.OrderId, ex.Message); }
        }

        private void HideGps(PlayerOrder order)
        {
            if (order.Gps == null) return;
            try { MyAPIGateway.Session.GPS.RemoveGps(order.Identity, order.Gps.Hash); }
            catch (Exception ex) { Log.Error("Orchestrator {0}: GPS removal for order {1}: {2}", entityId, order.OrderId, ex.Message); }
            order.Gps = null;
        }

        /// <summary>Block closed (removed, world unloaded): the markers go (the jobs stay in the saved state).</summary>
        private void HideAllPlayerGps()
        {
            foreach (var o in playerOrders.Values) HideGps(o);
        }

        private void CapturePlayerJobs(OrchestratorState s)
        {
            foreach (var pj in playerJobs.Values)
            {
                PlayerOrder o;
                if (!playerOrders.TryGetValue(pj.OrderId, out o)) continue;
                s.PlayerJobs.Add(new PlayerJobState
                {
                    JobId = pj.JobId, OrderId = pj.OrderId, Identity = o.Identity, Kind = o.Kind, Position = Vector3DData.FromVector3D(o.Position),
                    Name = o.Name, Announcement = pj.Announcement, ExpiresInSeconds = BlockSave.SecondsLeft(pj.ExpiresAt), Assigned = pj.Assigned,
                    RequiredDrone = pj.RequiredDrone,
                });
            }
        }

        private void RestorePlayerJobs(OrchestratorState s)
        {
            if (s.PlayerJobs == null) return;
            for (int i = 0; i < s.PlayerJobs.Count; i++)
            {
                var p = s.PlayerJobs[i];
                if (p.Announcement == null) continue;
                PlayerOrder o;
                if (!playerOrders.TryGetValue(p.OrderId, out o))
                {
                    o = new PlayerOrder { OrderId = p.OrderId, Identity = p.Identity, Kind = p.Kind, Type = p.Announcement.Type,
                                          Position = p.Position.ToVector3D(), Name = p.Name };
                    playerOrders[p.OrderId] = o;
                }
                o.Jobs++;
                if (p.Assigned) o.Assigned++;
                // The clock went on while the world was saved: an unconfirmed job gets the time it had left
                playerJobs[p.JobId] = new PlayerJob { JobId = p.JobId, OrderId = p.OrderId, Announcement = p.Announcement, RequiredDrone = p.RequiredDrone,
                                                      ExpiresAt = BlockSave.FromSecondsLeft(Math.Max(p.ExpiresInSeconds, 5)), Assigned = p.Assigned };
            }
            foreach (var o in playerOrders.Values)
            {
                o.Confirmed = o.Jobs > 0 && o.Assigned >= o.Jobs;
                ShowGps(o);
            }
        }
    }

    [ProtoContract]
    public class PlayerJobState
    {
        [ProtoMember(1)] public uint JobId;
        [ProtoMember(2)] public uint OrderId;
        [ProtoMember(3)] public long Identity;
        [ProtoMember(4)] public PointerKind Kind;
        [ProtoMember(5)] public Vector3DData Position;
        [ProtoMember(6)] public string Name;
        [ProtoMember(7)] public JobAnnouncement Announcement;
        [ProtoMember(8)] public double ExpiresInSeconds;
        [ProtoMember(9)] public bool Assigned;
        [ProtoMember(10)] public long RequiredDrone;
    }
}
