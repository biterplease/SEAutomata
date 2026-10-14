using ProtoBuf;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Collections;
using VRage.Game;
using VRageMath;

using Automata.Config;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.VirtualNetwork
{
    public sealed class MessageQueue
    {
        /// <summary>
        /// For use during world save/reload.
        /// </summary>
        [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
        private class MessageQueueSnapshot
        {
            [ProtoMember(1)]
            public List<TimestampedMessage> AllMessages;
            /// <summary>When the snapshot was taken (TimeUtil ms): message times are moved on by the time the world was saved.</summary>
            [ProtoMember(2)]
            public ulong SavedAt;
            public MessageQueueSnapshot() { }
        }
        /// <summary>A message in flight (also what a block saves of the messages it sent: see <see cref="CollectSent"/>).</summary>
        [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
        public class TimestampedMessage
        {
            [ProtoMember(1)]
            public byte[] Data;
            [ProtoMember(2)]
            public string StringData;
            /// <summary>
            /// EntityIds of IAI block that can receive this message.
            /// </summary>
            [ProtoMember(3)]
            public HashSet<long> ValidRecipients = null;
            [ProtoMember(4)]
            public ulong CreatedAt;
            [ProtoMember(5)]
            public ulong SentAt;
            [ProtoMember(6)]
            public long SenderId;
            [ProtoMember(7)]
            public long SenderOwnerId;
            [ProtoMember(8)]
            public uint MessageId;
            [ProtoMember(9)]
            public MessageSerializationMode SerializationMode;
            [ProtoMember(10)]
            public Channel Channel;
            [ProtoMember(11)]
            public IAIBlockType RecipientBlockType;
            [ProtoMember(12)]
            public bool RequiresAck;
            [ProtoMember(13)]
            public ushort PayloadTypeValue;

            public PayloadType PayloadType
            {
                get { return (PayloadType)PayloadTypeValue; }
                set { PayloadTypeValue = (ushort)value; }
            }

            public TimestampedMessage() { }
        }
        /// <summary>
        /// IAIBlockType flags since a single grid could have all 4 blocks.
        /// </summary>
        [Flags]
        public enum IAIBlockType : byte
        {
            None = 0,
            Drone = 1,
            Orchestrator = 2,
            LogisticsComputer = 4,
            ConstructionComputer = 8,
            MiningSurveyor = 16,
        }

        /// <summary>
        /// Cached antenna info for an entity
        /// </summary>
        public class AntennaInfo
        {
            public IMyRadioAntenna Antenna;
            public Vector3D Position;
            public double TransmitRange;
            public long OwnerId;
            /// <summary>
            /// EntityId of the IAI block that sends or receives messages, not antenna.
            /// </summary>
            public long IAIEntityId;
            public DateTime LastUpdate;
            public IAIBlockType BlockType;
            public bool IsStatic;

            public bool IsValid()
            {
                return AntennaUtil.Alive(Antenna) &&
                       Antenna.IsFunctional &&
                       Antenna.Enabled &&
                       Antenna.EnableBroadcasting &&
                       Antenna.IsWorking;
            }
            public AntennaInfo(long iaiEntityId, IMyRadioAntenna antenna, IAIBlockType blockType = IAIBlockType.None, bool isStatic = false)
            {
                OwnerId = antenna.OwnerId;
                IAIEntityId = iaiEntityId;
                BlockType = blockType;
                IsStatic = isStatic;
                Position = antenna.GetPosition();
                TransmitRange = AntennaUtil.Range(antenna);
                LastUpdate = DateTime.UtcNow;
                Antenna = antenna;
            }
            /// <summary>Position and range as they are now (the antenna moves with its grid, the player changes its range).</summary>
            public void RefreshLive()
            {
                if (!AntennaUtil.Alive(Antenna)) return;
                Position = Antenna.GetPosition();
                TransmitRange = AntennaUtil.Range(Antenna);
                OwnerId = Antenna.OwnerId;
                LastUpdate = DateTime.UtcNow;
            }

            public uint ToFlags()
            {
                uint flags = 0;
                if (IsStatic)
                    flags |= FLAG_IS_STATIC;
                if (IsValid())
                    flags |= FLAG_IS_VALID;
                flags |= MessageQueue.ToFlags(BlockType);
                return flags;
            }
            public BoundingBoxD GetAABB()
            {
                return CreateSphereAABB(Position, TransmitRange);
            }
        }

        public static uint ToFlags(IAIBlockType blockType)
        {
            uint flags = 0;
            if ((IAIBlockType.Drone & blockType) > 0)
                flags |= FLAG_BLOCK_TYPE_DRONE;
            if ((IAIBlockType.LogisticsComputer & blockType) > 0)
                flags |= FLAG_BLOCK_TYPE_LOGISTICS_COMPUTER;
            if ((IAIBlockType.Orchestrator & blockType) > 0)
                flags |= FLAG_BLOCK_TYPE_ORCHESTRATOR;
            if ((IAIBlockType.ConstructionComputer & blockType) > 0)
                flags |= FLAG_BLOCK_TYPE_CONSTRUCTION_COMPUTER;
            if ((IAIBlockType.MiningSurveyor & blockType) > 0)
                flags |= FLAG_BLOCK_TYPE_MINING_SURVEYOR;
            return flags;
        }







        private readonly MessageSerializationMode serializationMode;

        // Antenna cache - updated when entities register/update

        private readonly MyConcurrentDictionary<Channel, MyConcurrentQueue<TimestampedMessage>> _channelQueues;
        private readonly MyConcurrentDictionary<Channel, object> _channelLocks = new MyConcurrentDictionary<Channel, object>();
        private readonly MyConcurrentDictionary<Channel, MyConcurrentHashSet<long>> _channelSubscribers;
        private readonly MyConcurrentDictionary<long, DateTime> _lastReadTimes;
        private readonly MyConcurrentDictionary<long, TimeSpan> _readThrottleIntervals;
        private readonly MyConcurrentDictionary<ushort, MyConcurrentHashSet<long>> _messageAcknowledgments;
        private readonly MyConcurrentDictionary<long, MyConcurrentHashSet<uint>> _pendingAcknowledgments;
        private readonly IMyUtilitiesDelegate MyUtilitiesDelegate;
        private readonly IInterlockedDelegate InterlockedDelegate;
        private readonly object _exclusiveLock = new object();


        public const uint FLAG_BLOCK_TYPE_DRONE = 1 << 0;
        public const uint FLAG_BLOCK_TYPE_ORCHESTRATOR = 1 << 1;
        public const uint FLAG_BLOCK_TYPE_LOGISTICS_COMPUTER = 1 << 2;
        public const uint FLAG_BLOCK_TYPE_CONSTRUCTION_COMPUTER = 1 << 3;
        public const uint FLAG_BLOCK_TYPE_MINING_SURVEYOR = 1 << 4;
        public const uint FLAG_IS_STATIC = 1 << 5;
        /// <summary>
        /// IsValid() was true when added to the DAABBTree. Should still be checked once the AntennaInfo is retrieved.
        /// </summary>
        public const uint FLAG_IS_VALID = 1 << 4;
        private readonly MyDynamicAABBTreeD antennaTree = new MyDynamicAABBTreeD();
        /// <summary>The antenna network (relays through bases and drones, laser links), rebuilt every few seconds.</summary>
        public readonly RelayGraph Relays = new RelayGraph();
        private readonly TimeSpan _antennaTreeTimeout = TimeSpan.FromMinutes(30);
        /// <summary>IAI EntityId to DAABBTree proxy.</summary>
        private readonly MyConcurrentDictionary<long, int> iaiEntityIdToProxy = new MyConcurrentDictionary<long, int>();
        /// <summary>Reverse lookup of proxy id to IAI entity.</summary>
        private readonly MyConcurrentDictionary<int, long> proxyToEntityId = new MyConcurrentDictionary<int, long>();
        // cleanup caches
        private readonly HashSet<long> _cleanupBuffer = new HashSet<long>();
        private readonly List<AntennaInfo> _allAntennas = new List<AntennaInfo>();
        private readonly List<AntennaInfo> _recipientScan = new List<AntennaInfo>();

        private readonly TimeSpan _messageExpiration;
        private readonly TimeSpan _dlqMessageExpiration;
        private readonly object _cleanupLock = new object();
        private DateTime _lastCleanup = DateTime.UtcNow;
        private readonly TimeSpan _cleanupInterval;
        private int _messageCounter = 0;
        private static bool _isInitialized = false;
        private volatile bool _isShuttingDown = false;


        /// <summary>
        /// Cache from SenderOwnerId to Set{SenderReceiverId} to avoid calling the ownership api.
        /// </summary>
        private readonly MyConcurrentDictionary<long, MyConcurrentHashSet<long>> _sharingGroupCache = new MyConcurrentDictionary<long, MyConcurrentHashSet<long>>();
        private readonly IMessageQueueConfig _config;

        private static MessageQueue _instance;
        private static readonly object _instanceLock = new object();
        public static MessageQueue Instance
        {
            get
            {
                if (!_isInitialized)
                    throw new InvalidOperationException("MessageQueue must be initialized before use");
                return _instance;
            }
        }
        private MessageQueue(
            IMessageQueueConfig config = null,
            IMyUtilitiesDelegate myUtilitiesDelegate = null,
            InterlockedDelegate interlockedDelegate = null)
        {
            _config = config ?? new MessageQueueConfig();
            MyUtilitiesDelegate = myUtilitiesDelegate ?? new MyUtilitiesDelegate();
            InterlockedDelegate = interlockedDelegate ?? new InterlockedDelegate();
            serializationMode = _config.SerializationMode();
            _messageExpiration = TimeUtil.TickToTimeSpan(_config.MessageRetentionTicks());
            _dlqMessageExpiration = TimeUtil.TickToTimeSpan(_config.DlqMessageRetentionTicks());
            _cleanupInterval = TimeUtil.TickToTimeSpan(_config.MessageCleanupIntervalTicks());

            _channelQueues = new MyConcurrentDictionary<Channel, MyConcurrentQueue<TimestampedMessage>>();
            foreach (var channel in (Channel[])Enum.GetValues(typeof(Channel)))
            {
                _channelQueues.Add(channel, new MyConcurrentQueue<TimestampedMessage>());
                _channelLocks.Add(channel, new object());
            }
            _channelSubscribers = new MyConcurrentDictionary<Channel, MyConcurrentHashSet<long>>();
            _lastReadTimes = new MyConcurrentDictionary<long, DateTime>();
            _readThrottleIntervals = new MyConcurrentDictionary<long, TimeSpan>();
            _pendingAcknowledgments = new MyConcurrentDictionary<long, MyConcurrentHashSet<uint>>();
        }

        public static void Init(
             IMessageQueueConfig config = null,
            IMyUtilitiesDelegate myUtilitiesDelegate = null,
            InterlockedDelegate interlockedDelegate = null)
        {
            lock (_instanceLock)
            {
                if (_isInitialized)
                {
                    Log.Error("MessageQueue is already initialized.");
                    return;
                }
                _instance = new MessageQueue(config, myUtilitiesDelegate, interlockedDelegate);
                _isInitialized = true;
            }
        }

        /// <summary>
        /// Internal channel broadcast: posts payload to all current subscribers of <paramref name="channelOrdinal"/>
        /// without antenna range checks. Used for intra-scheduler routing (e.g. re-queuing a DroneReport).
        /// </summary>
        public ErrorCode SendMessage<T>(Channel channel, Message<T> message, long senderId, bool requiresAck = false)
            where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return ErrorCode.ShutdownInProgress;

            MyConcurrentHashSet<long> subscribers;
            if (!_channelSubscribers.TryGetValue(channel, out subscribers) || subscribers.Count == 0)
                return ErrorCode.NoSubscribers;

            var validRecipients = new HashSet<long>(subscribers);
            EnqueueMessage(ref message, validRecipients);
            return ErrorCode.None;
        }


        /// <summary>
        /// Send message to all valid recipients in range
        /// </summary>
        public ErrorCode BroadcastMessage<T>(
            IMyRadioAntenna senderAntenna,
            Message<T> message,
            bool enforceCommsRange = false) where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return ErrorCode.ShutdownInProgress;
            if (message == null)
            {
                return ErrorCode.MessageIsNull;
            }
            if (message.Channel == Channel.DIRECT_MESSAGE)
            {
                return ErrorCode.InvalidChannel;
            }

            // Check if there are any subscribers
            MyConcurrentHashSet<long> subscribers;
            if (!_channelSubscribers.TryGetValue(message.Channel, out subscribers) || subscribers.Count == 0)
            {
                if (Log.DebugEnabled)
                    Log.Debug("VNet: {0} #{1} from {2}: nobody subscribed to the channel", message.Channel, message.MessageId, message.SenderId);
                return ErrorCode.NoSubscribers;
            }

            // Every registered block, live positions: drones move (their registered position / tree box would be
            // stale), and the tree's user flags need ALL requested bits (a Drone | LogisticsComputer message matched none)
            _recipientScan.Clear();
            antennaTree.GetAll(_recipientScan, true);
            var validRecipients = new HashSet<long>();
            int registered = _recipientScan.Count, otherType = 0, invalid = 0, notShared = 0, outOfRange = 0, notListening = 0;
            for (int i = 0; i < _recipientScan.Count; i++)
            {
                AntennaInfo recipient = _recipientScan[i];
                if (recipient == null || recipient.IAIEntityId == message.SenderId) continue;
                if (message.RecipientBlockType != IAIBlockType.None && (recipient.BlockType & message.RecipientBlockType) == 0)
                {
                    otherType++;
                    continue;
                }
                // Only blocks listening on the channel: a registered block of the type that doesn't read it is no recipient
                if (!subscribers.Contains(recipient.IAIEntityId))
                {
                    notListening++;
                    continue;
                }
                if (!recipient.IsValid())
                {
                    invalid++;
                    continue;
                }
                recipient.RefreshLive();
                if (!CanShare(message.SenderOwnerId, recipient.OwnerId, recipient.Antenna))
                {
                    notShared++;
                    continue;
                }
                if (IsInCommunicationRange(message.SenderId, senderAntenna, ref recipient, checkBothRanges: enforceCommsRange))
                    validRecipients.Add(recipient.IAIEntityId);
                else outOfRange++;
            }
            _recipientScan.Clear();
            if (Log.DebugEnabled)
                Log.Debug("VNet: {0} {1} #{2} from {3} ({4:F0} m) to {5}: {6} recipient(s) of {7} registered ({8} other type, {9} not listening, {10} invalid, {11} not shared, {12} out of range)",
                          message.Channel, message.Payload != null ? message.Payload.GetType().Name : "null", message.MessageId, message.SenderId,
                          senderAntenna != null ? AntennaUtil.Range(senderAntenna) : 0f, message.RecipientBlockType, validRecipients.Count, registered,
                          otherType, notListening, invalid, notShared, outOfRange);
            if (validRecipients.Count == 0)
            {
                return ErrorCode.NoSubscribers;
            }
            EnqueueMessage(ref message, validRecipients);
            return ErrorCode.None;
        }

        private static PayloadType GetPayloadType(IMessagePayload payload)
        {
            if (payload == null)
                return PayloadType.None;
            if (payload is RelayMessage)
                return PayloadType.RelayMessage;
            if (payload is TaskAssignment)
                return PayloadType.TaskAssignment;
            if (payload is TaskAborted)
                return PayloadType.TaskAborted;
            if (payload is JobAnnouncement)
                return PayloadType.JobAnnouncement;
            if (payload is MiningSurveyDataBroadcast)
                return PayloadType.MiningSurveyDataBroadcast;
            if (payload is Auction)
                return PayloadType.Auction;
            if (payload is Bid)
                return PayloadType.Bid;
            if (payload is AuctionWinnerAnnouncement)
                return PayloadType.AuctionWinnerAnnouncement;
            if (payload is JobAnnouncementFailed)
                return PayloadType.JobAnnouncementFailed;
            if (payload is JobStatusReport)
                return PayloadType.JobStatusReport;
            return PayloadType.None;
        }

        private void EnqueueMessage<T>(ref Message<T> message, HashSet<long> validRecipients = null) where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return;
            var msg = new TimestampedMessage
            {
                Channel = message.Channel,
                CreatedAt = message.CreatedAt,
                SentAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = message.SenderId,
                ValidRecipients = validRecipients,
                SenderOwnerId = message.SenderOwnerId,
                MessageId = message.MessageId,
                SerializationMode = message.SerializationMode,
                RecipientBlockType = message.RecipientBlockType,
                RequiresAck = message.RequiresAck,
                PayloadType = GetPayloadType(message.Payload)
            };
            if (serializationMode == MessageSerializationMode.ProtoBuf)
                msg.Data = MyUtilitiesDelegate.SerializeToBinary<IMessagePayload>(message.Payload);
            else
                msg.StringData = MyUtilitiesDelegate.SerializeToXML(message.Payload);

            var channelLock = _channelLocks[message.Channel];
            lock (channelLock)
            {
                var queue = _channelQueues.GetOrAdd(message.Channel, _ => new MyConcurrentQueue<TimestampedMessage>());
                queue.Enqueue(msg);
            }
            if (msg.RequiresAck)
            {
                var pendingAcks = _pendingAcknowledgments.GetOrAdd(msg.SenderId, _ => new MyConcurrentHashSet<uint>());
                pendingAcks.Add(msg.MessageId);
            }
        }

        /// <summary>
        /// Checks if the antenna is in range.
        /// Designed to simulate script IGC: : An antenna can RECEIVE messages even if it’s
        /// transmit range is lower, but any messages it sends will not
        /// make it to the destination.
        /// When checkBothRanges is true, will check both antennas can reach each other.
        /// </summary>
        /// <param name="entityId">IAI Block (Drone, Scheduler, LC) id of sender.</param>
        /// <param name="antenna"></param>
        /// <param name="recipientInfo"></param>
        /// <param name="checkBothRanges">When true, will check both</param>
        /// <returns></returns>
        private bool IsInCommunicationRange(
            long entityId,
            IMyRadioAntenna antenna,
            ref AntennaInfo counterPartInfo,
            bool checkBothRanges = false
           )
        {
            var recipient = counterPartInfo.Antenna;

            if (recipient.EntityId == entityId)
                return false;

            if (!counterPartInfo.IsValid())
                return false;

            // Use DistanceSquared to avoid sqrt
            var antennaPos = antenna.GetPosition();
            double distanceToConterPartSquared = Vector3D.DistanceSquared(antennaPos, counterPartInfo.Position);
            double antennaRangeSquared = Math.Pow(AntennaUtil.Range(antenna), 2);

            if (checkBothRanges)
            {
                double recipientRangeSquared = Math.Pow(counterPartInfo.TransmitRange, 2);
                if (distanceToConterPartSquared <= antennaRangeSquared && distanceToConterPartSquared <= recipientRangeSquared) return true;
                return Relays.Reaches(antenna.CubeGrid, recipient.CubeGrid, antenna.OwnerId)
                    && Relays.Reaches(recipient.CubeGrid, antenna.CubeGrid, recipient.OwnerId);
            }

            // Simulate IGC: : An antenna can RECEIVE messages even if it’s
            // transmit range is lower, but any messages it sends will not
            // make it to the destination.
            if (distanceToConterPartSquared <= antennaRangeSquared) return true;
            // Relayed: the same base (connected grids), or other bases / drones in between (TODO/13)
            return Relays.Reaches(antenna.CubeGrid, recipient.CubeGrid, antenna.OwnerId);
        }

        public bool GetAntennaInfo(long entityId, out AntennaInfo antennaInfo)
        {
            int proxyId;
            if (!iaiEntityIdToProxy.TryGetValue(entityId, out proxyId))
            {
                antennaInfo = null;
                return false;
            }
            antennaInfo = antennaTree.GetUserData<AntennaInfo>(proxyId);
            return true;
        }

        /// <summary>
        /// Get recipients in range.
        /// Filters out the sender from the result list.
        /// Prefer this overload if you have userFlags.
        /// </summary>
        /// <param name="senderEntityId"></param>
        /// <param name="senderAntenna"></param>
        /// <param name="userFlags"></param>
        /// <param name="recipients"></param>
        public void GetRecipientsInRange(long senderEntityId, IMyRadioAntenna senderAntenna, List<AntennaInfo> recipients, uint userFlags = 0)
        {
            BoundingBoxD antennaAABB = CreateSphereAABB(senderAntenna.GetPosition(), AntennaUtil.Range(senderAntenna));
            antennaTree.OverlapAllBoundingBox(ref antennaAABB, recipients, userFlags, true);
            recipients.RemoveAll(r => r.IAIEntityId == senderEntityId);
        }

        /// <summary>
        /// Get recipients in range. Filters out the sender.
        /// Slightly faster overload since it does not populate a list, but action immediately with callback.
        /// Prefer this overload if you have no user flags.
        /// </summary>
        /// <param name="senderEntityId"></param>
        /// <param name="senderAntenna"></param>
        /// <param name="callback"></param>
        public void GetRecipientsInRange(long senderEntityId, IMyRadioAntenna senderAntenna, Action<AntennaInfo> callback)
        {
            BoundingSphereD antennaSphere = new BoundingSphereD(senderAntenna.GetPosition(), AntennaUtil.Range(senderAntenna));
            antennaTree.OverlapAllBoundingSphere<AntennaInfo>(ref antennaSphere, recipient =>
            {
                if (recipient.IAIEntityId != senderEntityId)
                {
                    callback(recipient);
                }
            });
        }

        public void RegisterAntenna(long iaiEntityId, IAIBlockType iaiBlockType, IMyRadioAntenna antenna, bool isStatic = false)
        {
            if (antenna == null || iaiEntityIdToProxy.ContainsKey(iaiEntityId))
                return;

            AntennaInfo antennaInfo = new AntennaInfo(iaiEntityId, antenna, iaiBlockType, isStatic);
            var aabb = antennaInfo.GetAABB();
            int proxyId = antennaTree.AddProxy(ref aabb, antennaInfo, antennaInfo.ToFlags(), true);
            iaiEntityIdToProxy[iaiEntityId] = proxyId;
            proxyToEntityId[proxyId] = iaiEntityId;
            Log.Debug("VNet: {0} {1} registered: antenna '{2}' ({3:F0} m){4}, {5} registered", iaiBlockType, iaiEntityId, antenna.CustomName,
                      AntennaUtil.Range(antenna), isStatic ? ", static" : "", iaiEntityIdToProxy.Count);
        }

        private bool RemoveAntennaProxy(int proxyId)
        {
            long entityId;
            if (!proxyToEntityId.TryGetValue(proxyId, out entityId))
                return false;
            antennaTree.RemoveProxy(proxyId);
            proxyToEntityId.Remove(proxyId);
            iaiEntityIdToProxy.Remove(entityId);
            return true;
        }

        public bool RemoveAntenna(long iaiEntityId)
        {
            int proxyId;
            if (!iaiEntityIdToProxy.TryGetValue(iaiEntityId, out proxyId))
                return false;
            antennaTree.RemoveProxy(proxyId);
            iaiEntityIdToProxy.Remove(iaiEntityId);
            proxyToEntityId.Remove(proxyId);
            Log.Debug("VNet: {0} unregistered, {1} registered", iaiEntityId, iaiEntityIdToProxy.Count);
            return true;
        }
        /// <summary>A block closed: off every channel (it is no recipient any more).</summary>
        public void UnsubscribeAll(long subscriberId)
        {
            foreach (var channel in (Channel[])Enum.GetValues(typeof(Channel)))
            {
                MyConcurrentHashSet<long> subscribers;
                if (_channelSubscribers.TryGetValue(channel, out subscribers)) subscribers.Remove(subscriberId);
            }
        }

        public bool AntennaMoved(long iaiEntityId, IMyRadioAntenna antenna)
        {
            int proxyId;
            if (!iaiEntityIdToProxy.TryGetValue(iaiEntityId, out proxyId))
                return false;
            AntennaInfo antennaInfo = antennaTree.GetUserData<AntennaInfo>(proxyId);

            var newPos = antenna.GetPosition();
            var newRadius = AntennaUtil.Range(antenna);
            var oldPos = antennaInfo.Position;
            antennaInfo.Position = newPos;
            antennaInfo.TransmitRange = newRadius;
            antennaInfo.LastUpdate = DateTime.UtcNow;

            var aabb = antennaInfo.GetAABB();
            antennaTree.MoveProxy(proxyId, ref aabb, oldPos - newPos);
            return true;
        }


        public static BoundingBoxD CreateSphereAABB(Vector3D sphereCenter, double sphereRadius)
        {
            return new BoundingBoxD(
                new Vector3D(sphereCenter.X - sphereRadius, sphereCenter.Y - sphereRadius, sphereCenter.Z - sphereRadius),
                new Vector3D(sphereCenter.X + sphereRadius, sphereCenter.Y + sphereRadius, sphereCenter.Z + sphereRadius)
            );
        }
        public static BoundingBoxD CreateSphereAABB(BoundingSphereD sphere)
        {
            return BoundingBoxD.CreateFromSphere(sphere);
        }


        /// <summary>
        /// Check if two entities can share information based on ownership
        /// </summary>
        private bool CanShare(long senderOwnerId, long recipientOwnerId, IMyTerminalBlock recipientBlock)
        {
            if (senderOwnerId == recipientOwnerId) return true;

            MyConcurrentHashSet<long> validRecipients;
            if (_sharingGroupCache.TryGetValue(senderOwnerId, out validRecipients))
            {
                if (validRecipients.Contains(recipientOwnerId))
                    return true;
            }
            else
            {
                validRecipients = new MyConcurrentHashSet<long>();
                _sharingGroupCache[senderOwnerId] = validRecipients;
            }
            if (recipientBlock != null)
            {
                var relation = recipientBlock.GetUserRelationToOwner(senderOwnerId);
                bool canShare = relation == MyRelationsBetweenPlayerAndBlock.Owner ||
                       relation == MyRelationsBetweenPlayerAndBlock.FactionShare ||
                       relation == MyRelationsBetweenPlayerAndBlock.Friends;
                if (canShare)
                    validRecipients.Add(recipientOwnerId);
                return canShare;
            }

            return false;
        }



        public ErrorCode ReadMessages<T>(
            long subscriberId,
            IMyRadioAntenna subscriberAntenna,
            Channel channel,
            List<Message<T>> outMessages,
            int maxMessages = 10,
            bool clear = true,
            PayloadType messageFilters = PayloadType.None)
            where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return ErrorCode.ShutdownInProgress;
            MyConcurrentHashSet<long> subscribers;
            if (!_channelSubscribers.TryGetValue(channel, out subscribers) || !subscribers.Contains(subscriberId))
                return ErrorCode.NotSubscribed;

            int proxyId; // just a validation check, proxyId not used until the loops
            if (!iaiEntityIdToProxy.TryGetValue(subscriberId, out proxyId))
                return ErrorCode.SubscriberAntennaNotRegistered;

            var subscriberInfo = antennaTree.GetUserData<AntennaInfo>(proxyId);
            subscriberInfo.RefreshLive();   // moved since it registered (drones)

            if (clear)
                outMessages.Clear();

            var channelLock = _channelLocks[channel];
            lock (channelLock)
            {
                MyConcurrentQueue<TimestampedMessage> queue;
                if (_channelQueues.TryGetValue(channel, out queue))
                {
                    var currentTime = DateTime.UtcNow;
                    var messagesToReEnqueue = new List<TimestampedMessage>();
                    var messagesForDLQ = new List<TimestampedMessage>();

                    var tempMessages = new List<TimestampedMessage>();
                    int messagesToProcess = 0;
                    int senderProxyId;
                    int dequeueScanCount = 0;
                    const int maxDequeueScan = 512;
                    // Collect all messages
                    TimestampedMessage msg;
                    int expired = 0, outOfRangeCount = 0;
                    while (messagesToProcess < maxMessages && dequeueScanCount < maxDequeueScan && queue.TryDequeue(out msg))
                    {
                        dequeueScanCount++;
                        if (IsMessageExpired(currentTime, msg.SentAt, _messageExpiration))
                        {
                            expired++;
                            if (msg.RequiresAck)
                            {
                                // if requires ack and expired, need to return it via DLQ
                                messagesForDLQ.Add(msg);
                            }
                            continue; // expired message is ignored, and dropped
                        }
                        bool isInternalMessage = !iaiEntityIdToProxy.TryGetValue(msg.SenderId, out senderProxyId) && msg.SenderOwnerId == 0;
                        if (!isInternalMessage && !iaiEntityIdToProxy.ContainsKey(msg.SenderId))
                        {
                            // Sender not registered (yet: blocks re-register after a load; or gone): kept until it expires
                            messagesToReEnqueue.Add(msg);
                            continue;
                        }

                        if (msg.ValidRecipients.Contains(subscriberId))
                        {
                            bool inRange;
                            if (isInternalMessage)
                            {
                                // Internal routing messages (SenderOwnerId==0, no antenna) bypass range checks.
                                inRange = true;
                            }
                            else
                            {
                                var senderInfo = antennaTree.GetUserData<AntennaInfo>(senderProxyId);
                                inRange = IsInCommunicationRange(msg.SenderId, senderInfo.Antenna, ref subscriberInfo, false);
                            }

                            if (inRange)
                            {
                                if (messageFilters != PayloadType.None && ((msg.PayloadType & messageFilters) == 0))
                                {
                                    messagesToReEnqueue.Add(msg);
                                    continue;
                                }
                                ++messagesToProcess;
                                tempMessages.Add(msg);
                                if (channel != Channel.DIRECT_MESSAGE)
                                {
                                    // Delivered once per recipient (no replays while it lives); kept for the others
                                    msg.ValidRecipients.Remove(subscriberId);
                                    if (msg.ValidRecipients.Count > 0) messagesToReEnqueue.Add(msg);
                                }
                            }
                            else
                            {
                                // caller is intended recipient, but out of range
                                outOfRangeCount++;
                                messagesToReEnqueue.Add(msg);
                            }
                        }
                        else
                        {
                            // message not for caller, re-enqueue for other receivers
                            messagesToReEnqueue.Add(msg);
                        }
                    }
                    foreach (var message in messagesToReEnqueue)
                        queue.Enqueue(message);

                    foreach (var message in messagesForDLQ)
                        ReturnUnackedMessage(message);
                    if ((expired > 0 || outOfRangeCount > 0 || tempMessages.Count > 0) && Log.DebugEnabled)
                        Log.Debug("VNet: {0} read {1}: {2} message(s), {3} out of range, {4} expired dropped, {5} queued", subscriberId, channel,
                                  tempMessages.Count, outOfRangeCount, expired, queue.Count);

                    // Process messages using cached ValidRecipients
                    foreach (var message in tempMessages)
                    {
                        T decoded;
                        if (serializationMode == MessageSerializationMode.ProtoBuf)
                            decoded = MyUtilitiesDelegate.SerializeFromBinary<T>(message.Data);
                        else
                            decoded = MyAPIGateway.Utilities.SerializeFromXML<T>(message.StringData);
                        if (message.RequiresAck)
                        {
                            AckMessage(message.SenderId, message.MessageId);
                        }
                        long recipientId = message.ValidRecipients.Count == 1 ? message.ValidRecipients.First() : 0;
                        var outMsg = new Message<T>
                        {
                            Payload = decoded,
                            MessageId = message.MessageId,
                            CreatedAt = message.CreatedAt,
                            SentAt = message.SentAt,
                            RecipientBlockType = message.RecipientBlockType,
                            SenderId = message.SenderId,
                            SenderOwnerId = message.SenderOwnerId,
                            Channel = message.Channel,
                            SerializationMode = message.SerializationMode,
                            RequiresAck = message.RequiresAck,

                        };
                        outMessages.Add(outMsg);
                    }
                }

            }

            return ErrorCode.None;
        }

        public void Subscribe(long subscriberId, Channel channel)
        {
            var subscribers = _channelSubscribers.GetOrAdd(channel, _ => new MyConcurrentHashSet<long>());
            subscribers.Add(subscriberId);
            _readThrottleIntervals.TryAdd(subscriberId, TimeSpan.FromMilliseconds(100));
        }

        public void Unsubscribe(long subscriberId, Channel channel)
        {
            MyConcurrentHashSet<long> subscribers;
            if (_channelSubscribers.TryGetValue(channel, out subscribers))
                subscribers.Remove(subscriberId);
        }

        private bool AckMessage(long senderId, uint messageId)
        {
            MyConcurrentHashSet<uint> pendingAcks;
            if (_pendingAcknowledgments.TryGetValue(senderId, out pendingAcks))
            {
                pendingAcks.Remove(messageId);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Sends unacked message back to the DEAD_LETTER_QUEUE, senders should be able to
        /// capture these to know if the expected recipient never received the message.
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        private void ReturnUnackedMessage(TimestampedMessage msg, MyConcurrentQueue<TimestampedMessage> queue = null)
        {
            if (queue == null)
            {
                queue = _channelQueues.GetOrAdd(Channel.DEAD_LETTER_QUEUE, _ => new MyConcurrentQueue<TimestampedMessage>());
            }
            queue.Enqueue(msg);
            // drop acknowledgement
            if (msg.RequiresAck)
            {
                MyConcurrentHashSet<uint> pendingAcks;
                if (_pendingAcknowledgments.TryGetValue(msg.SenderId, out pendingAcks))
                {
                    pendingAcks.Remove(msg.MessageId);
                }
            }
        }


        public void TryPerformCleanup()
        {
            CleanupStaleMessages();
            CleanupInvalidAntennas();
        }

        public int CleanupInvalidAntennas()
        {
            _cleanupBuffer.Clear();

            antennaTree.GetAll(_allAntennas, clear: true);

            foreach (var antennaInfo in _allAntennas)
            {
                bool shouldRemove = false;

                // Null checks
                if (antennaInfo?.Antenna == null)
                    shouldRemove = true;
                // Entity lifecycle checks
                else if (antennaInfo.Antenna.Closed || antennaInfo.Antenna.MarkedForClose)
                    shouldRemove = true;
                // Functional checks
                else if (!antennaInfo.IsValid())
                    shouldRemove = true;
                // Grid checks - grid was deleted/unloaded
                else if (antennaInfo.Antenna.CubeGrid == null || antennaInfo.Antenna.CubeGrid.Closed)
                    shouldRemove = true;
                // Timeout check - hasn't been updated in a long time (optional)
                else if ((DateTime.UtcNow - antennaInfo.LastUpdate).TotalMinutes > 30)
                    shouldRemove = true;

                if (shouldRemove)
                    _cleanupBuffer.Add(antennaInfo.IAIEntityId);
            }

            foreach (long entityId in _cleanupBuffer)
            {
                RemoveAntenna(entityId);
            }
            if (_cleanupBuffer.Count > 10)
            {
                int root = antennaTree.GetRoot();
                if (root != MyDynamicAABBTreeD.NullNode)
                {
                    antennaTree.Balance(root);
                }

            }
            return _cleanupBuffer.Count;
        }

        /// <summary>Returns true when the message is older than <paramref name="expiration"/>.</summary>
        private bool IsMessageExpired(DateTime currentTime, ulong sentAt, TimeSpan expiration)
        {
            return (currentTime - TimeUtil.TimestampToDateTime(sentAt)) > expiration;
        }

        private void CleanupStaleMessages()
        {
            var currentTime = DateTime.UtcNow;
            var dlqMessages = new List<TimestampedMessage>();

            // Process each channel
            foreach (var channel in _channelQueues.Keys.ToList())
            {
                // Skip DLQ - we'll handle it specially at the end
                if (channel == Channel.DEAD_LETTER_QUEUE)
                    continue;

                var channelLock = _channelLocks[channel];
                lock (channelLock)
                {

                    MyConcurrentQueue<TimestampedMessage> existingQueue;
                    if (!_channelQueues.TryGetValue(channel, out existingQueue))
                        continue;

                    // Create new queue and replace in dictionary
                    var newQueue = new MyConcurrentQueue<TimestampedMessage>();
                    _channelQueues[channel] = newQueue;

                    // Process old queue offline
                    TimestampedMessage message;
                    while (existingQueue.TryDequeue(out message))
                    {
                        if (IsMessageExpired(currentTime, message.SentAt, _messageExpiration))
                        {
                            // Expired message
                            if (message.RequiresAck)
                            {
                                // Requires ack - send to DLQ
                                dlqMessages.Add(message);
                            }
                            // else: drop it (do nothing)
                        }
                        else
                        {
                            // Not expired - keep it
                            newQueue.Enqueue(message);
                        }
                    }
                }
            }

            dlqMessages = dlqMessages.FindAll(m => !IsMessageExpired(currentTime, m.SentAt, _dlqMessageExpiration));
            // Handle DLQ messages
            if (dlqMessages.Count > 0)
            {
                var dlqLock = _channelLocks[Channel.DEAD_LETTER_QUEUE];
                lock (dlqLock)
                {
                    var dlqQueue = _channelQueues.GetOrAdd(
                        Channel.DEAD_LETTER_QUEUE,
                        _ => new MyConcurrentQueue<TimestampedMessage>()
                    );

                    foreach (var msg in dlqMessages)
                    {
                        ReturnUnackedMessage(msg, dlqQueue);
                    }
                }
            }
        }

        public void Reset()
        {
            _instance = null;
            _isInitialized = false;
        }

        public byte[] SerializeForSave()
        {
            CleanupStaleMessages();
            return SerializeAllMessages();
        }
        /// <summary>
        /// Serialize all in-flight messages across all channels for world save.
        /// Thread-safe: locks each channel during collection.
        /// </summary>
        public byte[] SerializeAllMessages()
        {
            var allMessages = new List<TimestampedMessage>();

            foreach (var channel in _channelQueues.Keys.ToList())
            {
                var channelLock = _channelLocks[channel];
                lock (channelLock)
                {
                    MyConcurrentQueue<TimestampedMessage> queue;
                    if (!_channelQueues.TryGetValue(channel, out queue))
                        continue;

                    var tempMessages = new List<TimestampedMessage>();
                    TimestampedMessage msg;

                    // Dequeue all to collect
                    while (queue.TryDequeue(out msg))
                    {
                        tempMessages.Add(msg);
                    }

                    // Add to snapshot
                    allMessages.AddRange(tempMessages);

                    // Re-enqueue all messages
                    foreach (var message in tempMessages)
                    {
                        queue.Enqueue(message);
                    }
                }
            }

            var snapshot = new MessageQueueSnapshot { AllMessages = allMessages, SavedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow) };
            Log.Debug("VNet: snapshot of {0} message(s) for the save", allMessages.Count);
            return MyUtilitiesDelegate.SerializeToBinary<MessageQueueSnapshot>(snapshot);
        }

        /// <summary>
        /// Deserialize and restore all in-flight messages on world load.
        /// Restores messages to their channels and pending acknowledgments.
        /// </summary>
        public void DeserializeAllMessages(byte[] data)
        {
            if (data == null || data.Length == 0)
                return;

            var snapshot = MyUtilitiesDelegate.SerializeFromBinary<MessageQueueSnapshot>(data);
            if (snapshot == null || snapshot.AllMessages == null)
                return;
            // The world sat saved for a while: messages keep the age they had at the save (else all would be expired)
            ulong now = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            ulong shift = snapshot.SavedAt > 0 && now > snapshot.SavedAt ? now - snapshot.SavedAt : 0;
            Log.Info("VNet: {0} message(s) restored from the save (times moved on {1:F0} s)", snapshot.AllMessages.Count, shift / 1000.0);

            foreach (var msg in snapshot.AllMessages)
            {
                if (msg == null) continue;
                msg.SentAt += shift;
                msg.CreatedAt += shift;
                var channelLock = _channelLocks[msg.Channel];
                lock (channelLock)
                {
                    var queue = _channelQueues.GetOrAdd(msg.Channel, _ => new MyConcurrentQueue<TimestampedMessage>());
                    queue.Enqueue(msg);
                }

                if (msg.RequiresAck)
                {
                    var pendingAcks = _pendingAcknowledgments.GetOrAdd(msg.SenderId, _ => new MyConcurrentHashSet<uint>());
                    pendingAcks.Add(msg.MessageId);
                }
            }
        }
        /// <summary>
        /// World save: copies of the messages 'senderId' sent that are still in flight (any channel, not expired), for
        /// the sender's own save data. Times are ages (ms before now) in the copies' SentAt / CreatedAt.
        /// </summary>
        public void CollectSent(long senderId, List<TimestampedMessage> into)
        {
            ulong now = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            var currentTime = DateTime.UtcNow;
            foreach (var kv in _channelQueues)
            {
                var channelLock = _channelLocks[kv.Key];
                lock (channelLock)
                {
                    foreach (var msg in kv.Value)
                    {
                        if (msg == null || msg.SenderId != senderId || IsMessageExpired(currentTime, msg.SentAt, _messageExpiration)) continue;
                        into.Add(new TimestampedMessage
                        {
                            Data = msg.Data,
                            StringData = msg.StringData,
                            ValidRecipients = msg.ValidRecipients != null ? new HashSet<long>(msg.ValidRecipients) : null,
                            CreatedAt = now > msg.CreatedAt ? now - msg.CreatedAt : 0,
                            SentAt = now > msg.SentAt ? now - msg.SentAt : 0,
                            SenderId = msg.SenderId,
                            SenderOwnerId = msg.SenderOwnerId,
                            MessageId = msg.MessageId,
                            SerializationMode = msg.SerializationMode,
                            Channel = msg.Channel,
                            RecipientBlockType = msg.RecipientBlockType,
                            RequiresAck = msg.RequiresAck,
                            PayloadTypeValue = msg.PayloadTypeValue,
                        });
                    }
                }
            }
        }

        /// <summary>World load: a block's saved in-flight messages (<see cref="CollectSent"/>) back in their queues, with the age they had.</summary>
        public int RestoreSent(List<TimestampedMessage> saved)
        {
            if (saved == null || saved.Count == 0) return 0;
            ulong now = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            int restored = 0;
            for (int i = 0; i < saved.Count; i++)
            {
                var msg = saved[i];
                if (msg == null) continue;
                msg.SentAt = now > msg.SentAt ? now - msg.SentAt : now;
                msg.CreatedAt = now > msg.CreatedAt ? now - msg.CreatedAt : now;
                if (msg.ValidRecipients == null) msg.ValidRecipients = new HashSet<long>();
                var channelLock = _channelLocks[msg.Channel];
                lock (channelLock)
                {
                    var queue = _channelQueues.GetOrAdd(msg.Channel, _ => new MyConcurrentQueue<TimestampedMessage>());
                    queue.Enqueue(msg);
                }
                restored++;
            }
            return restored;
        }

        public void PrepareForShutdown()
        {
            antennaTree.Clear();
            _isShuttingDown = true;
        }

    }
}