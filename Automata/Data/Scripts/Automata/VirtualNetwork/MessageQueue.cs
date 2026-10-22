using ProtoBuf;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRageMath;

using Automata.Config;
using Automata.Drone;
using Automata.Util;
using Automata.Util.Logging;

namespace Automata.VirtualNetwork
{
    /// <summary>
    /// The in-memory message bus of the mod (server only). No block registers with it and nothing is encoded:
    ///  - A sender hands over its grid's <see cref="GridAntennaReg"/> (grid, owner) and its block type with the message; a
    ///    reader hands over its own and the block type it reads as. Whether the reader is where the message went is the
    ///    <see cref="RelayGraph"/>'s answer (same group, the antenna network, a docked drone's connectors).
    ///  - A message lives MessageRetentionTicks (default 10 s) in its channel's log. A reader keeps a cursor per channel
    ///    (a sequence number): each read looks at what came after it, delivers what the reader may take - not its own,
    ///    its block type, owner / faction shares with the sender, reachable from the sender's grid - and moves on. A
    ///    message out of reach when the reader gets to it is missed.
    ///  - Whether anyone read a message is told by <see cref="Message{T}.Delivered"/> (senders that must be heard resend).
    /// The payload is the sender's object, shared by every reader: read-only once sent (VirtualNetwork/CLAUDE.md).
    /// </summary>
    public sealed class MessageQueue
    {
        /// <summary>
        /// What the world save holds (<see cref="Serialize"/> / <see cref="Deserialize"/>): the only place messages are encoded.
        /// </summary>
        [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
        private class MessageQueueSnapshot
        {
            [ProtoMember(1)]
            public List<SavedMessage> Messages;
            /// <summary>When the snapshot was taken (TimeUtil ms): message times are moved on by the time the world was saved.</summary>
            [ProtoMember(2)]
            public ulong SavedAt;
            /// <summary>The relay graph's grids (member 3 held per-antenna entries once).</summary>
            [ProtoMember(4)]
            public List<GridAntennaReg> Grids;
            public MessageQueueSnapshot() { }
        }
        /// <summary>A message in the snapshot: <see cref="TimestampedMessage"/> with the payload encoded.</summary>
        [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
        private class SavedMessage
        {
            [ProtoMember(1)] public byte[] Data;
            [ProtoMember(4)] public ulong CreatedAt;
            [ProtoMember(5)] public ulong SentAt;
            [ProtoMember(6)] public long SenderId;
            [ProtoMember(7)] public long SenderOwnerId;
            [ProtoMember(8)] public uint MessageId;
            [ProtoMember(10)] public Channel Channel;
            [ProtoMember(11)] public IAIBlockType RecipientBlockType;
            [ProtoMember(13)] public ushort PayloadTypeValue;
            [ProtoMember(17)] public long SenderGridId;
            [ProtoMember(18)] public bool BothRanges;
            [ProtoMember(19)] public int Deliveries;
            [ProtoMember(20)] public IAIBlockType SenderBlockType;
            [ProtoMember(21)] public long RecipientId;
            public SavedMessage() { }
        }
        /// <summary>
        /// A message in flight. The payload is the sender's object itself, shared by every reader (nothing is encoded
        /// or copied on the way): payloads are read-only from the moment they are sent, on both sides (a sender builds a
        /// new one to change it, a receiver copies what it wants to change).
        /// </summary>
        public class TimestampedMessage
        {
            /// <summary>Order of sending (readers' cursors).</summary>
            public long Seq;
            public IMessagePayload Payload;
            public ulong CreatedAt;
            public ulong SentAt;
            public long SenderId;
            public long SenderOwnerId;
            public uint MessageId;
            public Channel Channel;
            public IAIBlockType RecipientBlockType;
            /// <summary>0: any reader of the block type (a broadcast); else only the block with this entity id reads it (a directed message).</summary>
            public long RecipientId;
            public ushort PayloadTypeValue;
            #region Where it was sent from
            public long SenderGridId;
            /// <summary>What sent it (a docked drone reaches the groups its connectors hold).</summary>
            public IAIBlockType SenderBlockType;
            /// <summary>Both ends must reach each other (else only the sender's reach counts: a radio is heard beyond its own range).</summary>
            public bool BothRanges;
            #endregion
            /// <summary>How many readers took it.</summary>
            public int Deliveries;

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
            TargetDesignator = 32,
        }

        /// <summary>One channel: its messages in order of sending, and where each reader got to.</summary>
        private sealed class ChannelLog
        {
            public readonly object Lock = new object();
            public readonly List<TimestampedMessage> Items = new List<TimestampedMessage>();
            public readonly Dictionary<long, long> Cursors = new Dictionary<long, long>();
            /// <summary>Seq and send time of the newest message ever appended (the early exits of a read).</summary>
            public long LastSeq;
            public ulong LastSentAt;
        }

        private readonly ChannelLog[] _channels;
        private long _seq;
        /// <summary>The antenna network (relays through bases and drones, laser links).</summary>
        public readonly RelayGraph Relays = new RelayGraph();
        /// <summary>The message was sent and is still alive in the channel (readers can still take it).</summary>
        public bool InFlight<T>(Message<T> message) where T : class, IMessagePayload
        {
            if (message == null || message.Receipt == null) return false;
            ulong nowMs = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            return nowMs < message.Receipt.SentAt + (ulong)_messageExpiration.TotalMilliseconds;
        }

        private readonly Dictionary<KeyValuePair<long, long>, bool> _shareCache = new Dictionary<KeyValuePair<long, long>, bool>();
        private readonly TimeSpan _messageExpiration;
        private readonly object _cleanupLock = new object();
        private static bool _isInitialized = false;
        private volatile bool _isShuttingDown = false;
        private readonly IMessageQueueConfig _config;
        private readonly IMyUtilitiesDelegate MyUtilitiesDelegate;

        /// <summary>How long a message lives (ticks): a sender that must be heard sends again after this.</summary>
        public int LifetimeTicks { get; private set; }

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
        private MessageQueue(IMessageQueueConfig config = null, IMyUtilitiesDelegate myUtilitiesDelegate = null)
        {
            _config = config ?? new MessageQueueConfig();
            MyUtilitiesDelegate = myUtilitiesDelegate ?? new MyUtilitiesDelegate();
            LifetimeTicks = _config.MessageRetentionTicks();
            _messageExpiration = TimeUtil.TickToTimeSpan(LifetimeTicks);

            int max = 0;
            foreach (var channel in (Channel[])Enum.GetValues(typeof(Channel))) max = Math.Max(max, (int)channel);
            _channels = new ChannelLog[max + 1];
            for (int i = 0; i < _channels.Length; i++) _channels[i] = new ChannelLog();
        }

        public static void Init(IMessageQueueConfig config = null, IMyUtilitiesDelegate myUtilitiesDelegate = null)
        {
            lock (_instanceLock)
            {
                if (_isInitialized)
                {
                    Log.Error("MessageQueue is already initialized.");
                    return;
                }
                _instance = new MessageQueue(config, myUtilitiesDelegate);
                _isInitialized = true;
            }
        }

        #region Sending
        /// <summary>
        /// Sends the message from 'sender' (its grid's entry: see <see cref="GridAntennaReg.Ensure"/>; 'senderType': the sending
        /// block's type, a drone's messages also go through the connectors it is docked by). Nobody
        /// is looked up: readers in range take it while it lives. Returns None once it is in the channel; whether anyone
        /// took it is <see cref="Message{T}.Delivered"/>, later.
        /// </summary>
        public ErrorCode BroadcastMessage<T>(GridAntennaReg sender, Message<T> message, bool enforceCommsRange = false,
                                             IAIBlockType senderType = IAIBlockType.None) where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return ErrorCode.ShutdownInProgress;
            if (message == null)
                return ErrorCode.MessageIsNull;
            if (message.Channel == Channel.DIRECT_MESSAGE)
                return ErrorCode.InvalidChannel;
            if (sender == null || sender.GridId == 0)
                return ErrorCode.SenderAntennaNotRegistered;

            var log = _channels[(int)message.Channel];
            var msg = new TimestampedMessage
            {
                Channel = message.Channel,
                CreatedAt = message.CreatedAt,
                SentAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = message.SenderId,
                SenderOwnerId = message.SenderOwnerId,
                MessageId = message.MessageId,
                Payload = message.Payload,
                RecipientBlockType = message.RecipientBlockType,
                RecipientId = message.RecipientId,
                PayloadType = GetPayloadType(message.Payload),
                SenderGridId = sender.GridId,
                SenderBlockType = senderType,
                BothRanges = enforceCommsRange,
            };
            lock (log.Lock)
            {
                msg.Seq = ++_seq;
                log.Items.Add(msg);
                log.LastSeq = msg.Seq;
                log.LastSentAt = msg.SentAt;
            }
            message.Receipt = msg;
            if (Log.DebugEnabled) Log.Debug("VNet: {0} {1} #{2} from {3} (grid '{4}') to {5}", message.Channel, message.Payload != null ? message.Payload.GetType().Name : "null", message.MessageId, message.SenderId, sender.GridName, message.RecipientBlockType);
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
            if (payload is PlayerAlert)
                return PayloadType.PlayerAlert;
            if (payload is DesignatorOrderUpdate)
                return PayloadType.DesignatorOrderUpdate;
            return PayloadType.None;
        }

        #endregion

        #region Reading
        /// <summary>
        /// Takes what came to 'readerId' (a block of type 'readerType', on the grid 'reader': see <see cref="GridAntennaReg.Ensure"/>)
        /// on 'channel' since its last read, at most 'maxMessages'. A reader reads a channel with one payload filter.
        /// </summary>
        public ErrorCode ReadMessages<T>(
            long readerId,
            IAIBlockType readerType,
            GridAntennaReg reader,
            Channel channel,
            List<Message<T>> outMessages,
            int maxMessages = 10,
            bool clear = true,
            PayloadType messageFilters = PayloadType.None)
            where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return ErrorCode.ShutdownInProgress;
            if (clear)
                outMessages.Clear();
            if (maxMessages <= 0 || reader == null || reader.GridId == 0)
                return reader == null || reader.GridId == 0 ? ErrorCode.RecipientNotValid : ErrorCode.TryAgainLater;

            var log = _channels[(int)channel];
            ulong nowMs = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            ulong lifeMs = (ulong)_messageExpiration.TotalMilliseconds;
            ulong cutoff = nowMs > lifeMs ? nowMs - lifeMs : 0;
            int outOfRange = 0, taken = 0;
            lock (log.Lock)
            {
                long cursor;
                log.Cursors.TryGetValue(readerId, out cursor);
                // Early exits, before any work (no scan): the channel never had a message / nothing new since
                // this reader's last read / everything new has expired already
                if (log.LastSeq <= cursor) return ErrorCode.TryAgainLater;
                if (log.LastSentAt < cutoff)
                {
                    log.Cursors[readerId] = log.LastSeq;
                    return ErrorCode.TryAgainLater;
                }
                var items = log.Items;
                for (int i = FirstAfter(items, cursor); i < items.Count && taken < maxMessages; i++)
                {
                    var m = items[i];
                    cursor = m.Seq;
                    if (m.SentAt < cutoff || m.SenderId == readerId) continue;
                    if (m.RecipientId != 0 && m.RecipientId != readerId) continue;   // a directed message: not for this reader
                    if (m.RecipientBlockType != IAIBlockType.None && (m.RecipientBlockType & readerType) == 0) continue;
                    if (messageFilters != PayloadType.None && (m.PayloadType & messageFilters) == 0) continue;
                    if (!CanShare(m.SenderOwnerId, reader.OwnerId)) continue;
                    if (!InRange(m, reader, readerType)) { outOfRange++; continue; }
                    var payload = m.Payload as T;   // the sender's object, shared: read-only
                    if (payload == null)
                    {
                        if (Log.DebugEnabled) Log.Debug("VNet: {0} #{1} from {2}: payload is {3}, not {4}: dropped", channel, m.MessageId, m.SenderId, m.Payload != null ? m.Payload.GetType().Name : "null", typeof(T).Name);
                        continue;
                    }
                    m.Deliveries++;
                    taken++;
                    outMessages.Add(new Message<T>
                    {
                        Payload = payload,
                        MessageId = m.MessageId,
                        CreatedAt = m.CreatedAt,
                        SentAt = m.SentAt,
                        RecipientBlockType = m.RecipientBlockType,
                        RecipientId = m.RecipientId,
                        SenderId = m.SenderId,
                        SenderOwnerId = m.SenderOwnerId,
                        Channel = m.Channel,
                    });
                }
                log.Cursors[readerId] = cursor;
            }
            if ((taken > 0 || outOfRange > 0) && Log.DebugEnabled) Log.Debug("VNet: {0} read {1}: {2} message(s), {3} out of range", readerId, channel, taken, outOfRange);
            return taken > 0 ? ErrorCode.None : ErrorCode.TryAgainLater;
        }

        // Index of the first message sent after 'cursor' (messages are in order of sending)
        private static int FirstAfter(List<TimestampedMessage> items, long cursor)
        {
            int lo = 0, hi = items.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (items[mid].Seq <= cursor) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>
        /// Is the reader where the message went? The sender's grid reaches the reader's (same group of grids: always; else
        /// through the antenna network, see <see cref="RelayGraph"/>; a drone on either end also counts the groups its
        /// connectors hold). Radio edges need both antennas to reach each other, so reach is symmetric (enforceCommsRange is moot).
        /// </summary>
        private const bool DOCKED_DRONE_TALKS_TO_ALL = true;

        private bool InRange(TimestampedMessage m, GridAntennaReg reader, IAIBlockType readerType)
        {
            // A docked drone is reached through (and reaches) the groups its connectors hold: with the orchestrator only, or
            // with every block type (job reports go to the computers too)
            // (a target designator is mobile like a drone - a "fleet leader" - and docks like one)
            bool senderDrone = (m.SenderBlockType == IAIBlockType.Drone || m.SenderBlockType == IAIBlockType.TargetDesignator) && (DOCKED_DRONE_TALKS_TO_ALL || readerType == IAIBlockType.Orchestrator);
            bool readerDrone = (readerType == IAIBlockType.Drone || readerType == IAIBlockType.TargetDesignator) && (DOCKED_DRONE_TALKS_TO_ALL || m.SenderBlockType == IAIBlockType.Orchestrator);
            return Relays.Reaches(m.SenderGridId, reader.GridId, m.SenderOwnerId, senderDrone, readerDrone);
        }

        // Owner or faction of the sender (cached; cleared with the cleanup)
        private bool CanShare(long senderOwnerId, long readerOwnerId)
        {
            if (senderOwnerId == readerOwnerId) return true;
            if (senderOwnerId == 0 || readerOwnerId == 0) return false;
            var key = new KeyValuePair<long, long>(senderOwnerId, readerOwnerId);
            bool ok;
            if (!_shareCache.TryGetValue(key, out ok))
            {
                ok = AnchorDirectory.IsAllowed(senderOwnerId, readerOwnerId);
                _shareCache[key] = ok;
            }
            return ok;
        }

        /// <summary>A block closed: its read positions dropped.</summary>
        public void ReaderClosed(long readerId)
        {
            for (int i = 0; i < _channels.Length; i++)
            {
                var log = _channels[i];
                lock (log.Lock) log.Cursors.Remove(readerId);
            }
        }
        #endregion

        #region Cleanup
        public void TryPerformCleanup()
        {
            ulong nowMs = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            ulong lifeMs = (ulong)_messageExpiration.TotalMilliseconds;
            ulong cutoff = nowMs > lifeMs ? nowMs - lifeMs : 0;
            for (int i = 0; i < _channels.Length; i++)
            {
                var log = _channels[i];
                lock (log.Lock)
                {
                    int n = 0;
                    while (n < log.Items.Count && log.Items[n].SentAt < cutoff) n++;
                    if (n > 0) log.Items.RemoveRange(0, n);
                }
            }
            _shareCache.Clear();
        }
        #endregion

        public void Reset()
        {
            _instance = null;
            _isInitialized = false;
        }

        #region Save
        /// <summary>
        /// World save (session SaveData): every message in flight, any channel, the payloads encoded here and nowhere else,
        /// and the relay graph's grids. Expired messages are dropped first.
        /// </summary>
        public byte[] Serialize()
        {
            TryPerformCleanup();
            var saved = new List<SavedMessage>();
            for (int i = 0; i < _channels.Length; i++)
            {
                var log = _channels[i];
                lock (log.Lock)
                {
                    foreach (var msg in log.Items)
                    {
                        if (msg == null || msg.Payload == null) continue;
                        saved.Add(new SavedMessage
                        {
                            Data = MyUtilitiesDelegate.SerializeToBinary<IMessagePayload>(msg.Payload),
                            CreatedAt = msg.CreatedAt,
                            SentAt = msg.SentAt,
                            SenderId = msg.SenderId,
                            SenderOwnerId = msg.SenderOwnerId,
                            MessageId = msg.MessageId,
                            Channel = msg.Channel,
                            RecipientBlockType = msg.RecipientBlockType,
                            RecipientId = msg.RecipientId,
                            PayloadTypeValue = msg.PayloadTypeValue,
                            SenderGridId = msg.SenderGridId,
                            SenderBlockType = msg.SenderBlockType,
                            BothRanges = msg.BothRanges,
                            Deliveries = msg.Deliveries,
                        });
                    }
                }
            }
            if (Log.DebugEnabled) Log.Debug("VNet: snapshot of {0} message(s), {1} grid(s) for the save", saved.Count, Relays.Count);
            return MyUtilitiesDelegate.SerializeToBinary<MessageQueueSnapshot>(
                new MessageQueueSnapshot { Messages = saved, SavedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow), Grids = Relays.Snapshot() });
        }

        /// <summary>
        /// World load (session start): the saved messages back in their channels, with the age they had at the save, and
        /// the relay graph's grids (its world scan is due a minute later, not at once).
        /// </summary>
        public void Deserialize(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            var snapshot = MyUtilitiesDelegate.SerializeFromBinary<MessageQueueSnapshot>(data);
            if (snapshot == null) return;
            Relays.Restore(snapshot.Grids, MyAPIGateway.Session.GameplayFrameCounter);
            if (snapshot.Messages == null) return;
            // The world sat saved for a while: messages keep the age they had at the save (else all would be expired)
            ulong now = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            ulong shift = snapshot.SavedAt > 0 && now > snapshot.SavedAt ? now - snapshot.SavedAt : 0;
            int restored = 0;
            foreach (var saved in snapshot.Messages)
            {
                if (saved == null) continue;
                var payload = DecodePayload((PayloadType)saved.PayloadTypeValue, saved.Data);
                if (payload == null)
                {
                    if (Log.DebugEnabled) Log.Debug("VNet: saved message #{0} on {1} ({2}): payload could not be read, dropped", saved.MessageId, saved.Channel, (PayloadType)saved.PayloadTypeValue);
                    continue;
                }
                var msg = new TimestampedMessage
                {
                    Payload = payload,
                    CreatedAt = saved.CreatedAt + shift,
                    SentAt = saved.SentAt + shift,
                    SenderId = saved.SenderId,
                    SenderOwnerId = saved.SenderOwnerId,
                    MessageId = saved.MessageId,
                    Channel = saved.Channel,
                    RecipientBlockType = saved.RecipientBlockType,
                    RecipientId = saved.RecipientId,
                    PayloadTypeValue = saved.PayloadTypeValue,
                    SenderGridId = saved.SenderGridId,
                    SenderBlockType = saved.SenderBlockType,
                    BothRanges = saved.BothRanges,
                    Deliveries = saved.Deliveries,
                };
                var log = _channels[(int)msg.Channel];
                lock (log.Lock)
                {
                    msg.Seq = ++_seq;
                    log.Items.Add(msg);
                    log.LastSeq = msg.Seq;
                    if (msg.SentAt > log.LastSentAt) log.LastSentAt = msg.SentAt;
                }
                restored++;
            }
            if (Log.DebugEnabled) Log.Debug("VNet: {0} message(s) restored from the save (times moved on {1:F0} s)", restored, shift / 1000.0);
        }

        // The payload class by its type tag (GetPayloadType's counterpart)
        private IMessagePayload DecodePayload(PayloadType type, byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                switch (type)
                {
                    case PayloadType.RelayMessage: return MyUtilitiesDelegate.SerializeFromBinary<RelayMessage>(data);
                    case PayloadType.TaskAssignment: return MyUtilitiesDelegate.SerializeFromBinary<TaskAssignment>(data);
                    case PayloadType.TaskAborted: return MyUtilitiesDelegate.SerializeFromBinary<TaskAborted>(data);
                    case PayloadType.JobAnnouncement: return MyUtilitiesDelegate.SerializeFromBinary<JobAnnouncement>(data);
                    case PayloadType.MiningSurveyDataBroadcast: return MyUtilitiesDelegate.SerializeFromBinary<MiningSurveyDataBroadcast>(data);
                    case PayloadType.Auction: return MyUtilitiesDelegate.SerializeFromBinary<Auction>(data);
                    case PayloadType.Bid: return MyUtilitiesDelegate.SerializeFromBinary<Bid>(data);
                    case PayloadType.AuctionWinnerAnnouncement: return MyUtilitiesDelegate.SerializeFromBinary<AuctionWinnerAnnouncement>(data);
                    case PayloadType.JobAnnouncementFailed: return MyUtilitiesDelegate.SerializeFromBinary<JobAnnouncementFailed>(data);
                    case PayloadType.JobStatusReport: return MyUtilitiesDelegate.SerializeFromBinary<JobStatusReport>(data);
                    case PayloadType.PlayerAlert: return MyUtilitiesDelegate.SerializeFromBinary<PlayerAlert>(data);
                    case PayloadType.DesignatorOrderUpdate: return MyUtilitiesDelegate.SerializeFromBinary<DesignatorOrderUpdate>(data);
                    default: return null;
                }
            }
            catch (Exception ex)
            {
                if (Log.DebugEnabled) Log.Debug("VNet: payload {0} could not be decoded: {1}", type, ex.Message);
                return null;
            }
        }

        #endregion

        public void PrepareForShutdown()
        {
            Relays.Clear();
            _isShuttingDown = true;
        }
    }
}
