using ImprovedAI.Config;
using ImprovedAI.Util;
using ImprovedAI.Util.Logging;
using ProtoBuf;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Collections;
using VRage.Game;
using VRageMath;


namespace ImprovedAI.VirtualNetwork
{
        [Flags]
        public enum PayloadType : ushort
        {
            None = 0,
            DroneReport = 1,
            TaskAssignment = 2,
            LogisticsUpdate = 4,
            InventoryRequisition = 8,
            RelayMessage = 16,
            TaskAborted = 32,
            TaskAnnouncement = 64,
            TaskBid = 128,
            TaskFulfillmentLost = 256,
        }
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
            public MessageQueueSnapshot() { }
        }
        [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
        private class TimestampedMessage
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
        /// IAIBlockType flags since a single grid could have all 3 blocks.
        /// </summary>
        [Flags]
        public enum IAIBlockType : byte
        {
            None = 0,
            Drone = 1,
            Scheduler = 2,
            LogisticsComputer = 4
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
                return Antenna != null &&
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
                TransmitRange = antenna.Radius;
                LastUpdate = DateTime.UtcNow;
                Antenna = antenna;
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
            if ((IAIBlockType.Scheduler & blockType) > 0)
                flags |= FLAG_BLOCK_TYPE_SCHEDULER;
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
        public const uint FLAG_BLOCK_TYPE_SCHEDULER = 1 << 1;
        public const uint FLAG_BLOCK_TYPE_LOGISTICS_COMPUTER = 1 << 2;
        public const uint FLAG_IS_STATIC = 1 << 3;
        /// <summary>
        /// IsValid() was true when added to the DAABBTree. Should still be checked once the AntennaInfo is retrieved.
        /// </summary>
        public const uint FLAG_IS_VALID = 1 << 4;
        private readonly MyDynamicAABBTreeD antennaTree = new MyDynamicAABBTreeD();
        private readonly TimeSpan _antennaTreeTimeout = TimeSpan.FromMinutes(30);
        /// <summary>IAI EntityId to DAABBTree proxy.</summary>
        private readonly MyConcurrentDictionary<long, int> iaiEntityIdToProxy = new MyConcurrentDictionary<long, int>();
        /// <summary>Reverse lookup of proxy id to IAI entity.</summary>
        private readonly MyConcurrentDictionary<int, long> proxyToEntityId = new MyConcurrentDictionary<int, long>();
        // cleanup caches
        private readonly HashSet<long> _cleanupBuffer = new HashSet<long>();
        private readonly List<AntennaInfo> _allAntennas = new List<AntennaInfo>();

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
        public ErrorCode SendMessage<T>(ushort channelOrdinal, T payload, long senderId, bool requiresAck = false)
            where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return ErrorCode.ShutdownInProgress;

            var channel = (Channel)channelOrdinal;
            MyConcurrentHashSet<long> subscribers;
            if (!_channelSubscribers.TryGetValue(channel, out subscribers) || subscribers.Count == 0)
                return ErrorCode.NoSubscribers;

            var validRecipients = new HashSet<long>(subscribers);

            var msg = new Message<T>
            {
                MessageId = (uint)InterlockedDelegate.Increment(ref _messageCounter),
                Payload = payload,
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = senderId,
                SenderOwnerId = 0,
                Channel = channel,
                RequiresAck = requiresAck,
                SerializationMode = serializationMode,
                RecipientBlockType = IAIBlockType.None,
            };
            EnqueueMessage(ref msg, validRecipients);
            return ErrorCode.None;
        }

        /// <summary>
        /// Sends direct message to a recipient on the DIRECT_MESSAGE channel.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="senderAntenna"></param>
        /// <param name="message"></param>
        /// <param name="enforceCommsRange">
        ///     When true, will require an acknowledgement of the message by the recipient.
        ///     Both the sender and recipient antenna need to be in communication range of each other.
        ///     If false, only the sender needs to be able to reach the recipient.
        /// </param>
        /// <returns></returns>
        public ErrorCode SendDirectMessage<T>(
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
            if (message.Channel != Channel.DIRECT_MESSAGE)
            {
                return ErrorCode.InvalidChannel;
            }

            int recipientProxyId;
            if (!iaiEntityIdToProxy.TryGetValue(message.RecipientId, out recipientProxyId))
            {
                return ErrorCode.RecipientNotRegistered;
            }

            var senderPos = senderAntenna.GetPosition();
            List<AntennaInfo> recipientInfos = new List<AntennaInfo>();
            GetRecipientsInRange(message.SenderId, senderAntenna, recipientInfos, ToFlags(message.RecipientBlockType));

            AntennaInfo recipientInfo;
            for (int i = 0; i < recipientInfos.Count; i++)
            {
                recipientInfo = recipientInfos[i];
                if (recipientInfo.IAIEntityId != message.RecipientId)
                    continue;

                if (!CanShare(message.SenderOwnerId, recipientInfo.OwnerId, recipientInfo.Antenna))
                {
                    return ErrorCode.RecipientNotFriendly;
                }

                if (!recipientInfo.IsValid())
                {
                    return ErrorCode.RecipientNotValid;
                }
                if (IsInCommunicationRange(message.SenderId, senderAntenna, ref recipientInfo, checkBothRanges: enforceCommsRange))
                {
                    EnqueueMessage(ref message, new HashSet<long> { recipientInfo.IAIEntityId });
                    return ErrorCode.None;
                }
                else
                {
                    return ErrorCode.RecipientNotInRange;
                }
            }

            return ErrorCode.RecipientNotFound;
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
                return ErrorCode.NoSubscribers;
            }

            List<AntennaInfo> recipients = new List<AntennaInfo>();
            GetRecipientsInRange(message.SenderId, senderAntenna, recipients, ToFlags(message.RecipientBlockType));

            var validRecipients = new HashSet<long>();
            for (int i = 0; i < recipients.Count; i++)
            {
                AntennaInfo recipient = recipients[i];
                if (!CanShare(message.SenderOwnerId, recipient.OwnerId, recipients[i].Antenna))
                    continue;
                if (!recipient.IsValid())
                    continue;
                if (IsInCommunicationRange(message.SenderId, senderAntenna, ref recipient, checkBothRanges: enforceCommsRange))
                    validRecipients.Add(recipient.IAIEntityId);
            }
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
            if (payload is DroneReport)
                return PayloadType.DroneReport;
            if (payload is RelayMessage)
                return PayloadType.RelayMessage;
            if (payload is InventoryRequisition)
                return PayloadType.InventoryRequisition;
            if (payload is LogisticsUpdate)
                return PayloadType.LogisticsUpdate;
            if (payload is TaskAssignment)
                return PayloadType.TaskAssignment;
            if (payload is TaskAborted)
                return PayloadType.TaskAborted;
            if (payload is TaskAnnouncement)
                return PayloadType.TaskAnnouncement;
            if (payload is TaskBid)
                return PayloadType.TaskBid;
            if (payload is TaskFulfillmentLost)
                return PayloadType.TaskFulfillmentLost;
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
            double antennaRangeSquared = Math.Pow(antenna.Radius, 2);

            if (checkBothRanges)
            {
                double recipientRangeSquared = Math.Pow(counterPartInfo.TransmitRange, 2);
                return distanceToConterPartSquared <= antennaRangeSquared && distanceToConterPartSquared <= recipientRangeSquared;
            }

            // Simulate IGC: : An antenna can RECEIVE messages even if it’s
            // transmit range is lower, but any messages it sends will not
            // make it to the destination.
            return distanceToConterPartSquared <= antennaRangeSquared;
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
            BoundingBoxD antennaAABB = CreateSphereAABB(senderAntenna.GetPosition(), senderAntenna.Radius);
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
            BoundingSphereD antennaSphere = new BoundingSphereD(senderAntenna.GetPosition(), senderAntenna.Radius);
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
            return true;
        }
        public bool AntennaMoved(long iaiEntityId, IMyRadioAntenna antenna)
        {
            int proxyId;
            if (!iaiEntityIdToProxy.TryGetValue(iaiEntityId, out proxyId))
                return false;
            AntennaInfo antennaInfo = antennaTree.GetUserData<AntennaInfo>(proxyId);

            var newPos = antenna.GetPosition();
            var newRadius = antenna.Radius;
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
                    while (messagesToProcess < maxMessages && dequeueScanCount < maxDequeueScan && queue.TryDequeue(out msg))
                    {
                        dequeueScanCount++;
                        if (IsMessageExpired(currentTime, msg.SentAt, _messageExpiration))
                        {
                            if (msg.RequiresAck)
                            {
                                // if requires ack and expired, need to return it via DLQ
                                messagesForDLQ.Add(msg);
                            }
                            continue; // expired message is ignored, and dropped
                        }
                        bool isInternalMessage = !iaiEntityIdToProxy.TryGetValue(msg.SenderId, out senderProxyId) && msg.SenderOwnerId == 0;
                        if (!isInternalMessage && !iaiEntityIdToProxy.ContainsKey(msg.SenderId))
                            continue; // sender antenna no longer registered, drop message and continue

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
                                    // message was not direct, resend so other callers can read
                                    messagesToReEnqueue.Add(msg);
                                }
                            }
                            else
                            {
                                // caller is intended recipient, but out of range
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
                            RecipientId = recipientId,
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

            var snapshot = new MessageQueueSnapshot { AllMessages = allMessages };
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

            foreach (var msg in snapshot.AllMessages)
            {
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
        public void PrepareForShutdown()
        {
            antennaTree.Clear();
            _isShuttingDown = true;
        }

    }
}