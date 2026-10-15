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
    ///  - A sender hands over its antenna's <see cref="AntennaReg"/> (position, range, grid, owner) with the message; the
    ///    queue notes where and how far it was sent from. A reader hands over its own and the block type it reads as.
    ///    Both go on to the <see cref="RelayGraph"/>, which keeps one entry per antenna id.
    ///  - A message lives MessageRetentionTicks (default 10 s) in its channel's log. A reader keeps a cursor per channel
    ///    (a sequence number): each read looks at what came after it, delivers what the reader may take - not its own,
    ///    its block type, owner / faction shares with the sender, in range of the sender's recorded position (its radius,
    ///    or the relay network) - and moves on. A message out of range when the reader gets to it is missed.
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
            /// <summary>The relay graph's antennas.</summary>
            [ProtoMember(3)]
            public List<AntennaReg> Antennas;
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
            [ProtoMember(14)] public long SenderAntennaId;
            [ProtoMember(15)] public Vector3DData SenderPosition;
            [ProtoMember(16)] public double SenderRange;
            [ProtoMember(17)] public long SenderGridId;
            [ProtoMember(18)] public bool BothRanges;
            [ProtoMember(19)] public int Deliveries;
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
            public ushort PayloadTypeValue;
            #region Where it was sent from
            public long SenderAntennaId;
            public Vector3D SenderPosition;
            public double SenderRange;
            public long SenderGridId;
            /// <summary>Both antennas must reach each other (else only the sender's radius counts: a radio hears beyond its own).</summary>
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

        /// <summary>Register (or refresh) an antenna in the relay graph. Static ones are only updated at its fallback rebuild.</summary>
        public void RegisterAntenna(AntennaReg reg) { if (reg != null) Relays.Register(reg); }
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
        /// Sends the message from 'sender' (the sender's antenna as it is now: see <see cref="AntennaReg.Sync"/>). Nobody
        /// is looked up: readers in range take it while it lives. Returns None once it is in the channel; whether anyone
        /// took it is <see cref="Message{T}.Delivered"/>, later.
        /// </summary>
        public ErrorCode BroadcastMessage<T>(AntennaReg sender, Message<T> message, bool enforceCommsRange = false) where T : class, IMessagePayload
        {
            if (_isShuttingDown)
                return ErrorCode.ShutdownInProgress;
            if (message == null)
                return ErrorCode.MessageIsNull;
            if (message.Channel == Channel.DIRECT_MESSAGE)
                return ErrorCode.InvalidChannel;
            if (sender == null || !sender.Working)
                return ErrorCode.SenderAntennaNotRegistered;

            Relays.Register(sender);
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
                PayloadType = GetPayloadType(message.Payload),
                SenderAntennaId = sender.AntennaId,
                SenderPosition = sender.Pos,
                SenderRange = sender.Range,
                SenderGridId = sender.GridId,
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
            if (Log.DebugEnabled) Log.Debug("VNet: {0} {1} #{2} from {3} ({4:F0} m) to {5}", message.Channel, message.Payload != null ? message.Payload.GetType().Name : "null",
                          message.MessageId, message.SenderId, sender.Range, message.RecipientBlockType);
            return ErrorCode.None;
        }

        /// <summary>For code that has the antenna block, not a registration (a throwaway one is made).</summary>
        public ErrorCode BroadcastMessage<T>(IMyRadioAntenna antenna, Message<T> message, bool enforceCommsRange = false) where T : class, IMessagePayload
        {
            return BroadcastMessage(antenna != null ? AntennaReg.Make(antenna) : null, message, enforceCommsRange);
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

        #endregion

        #region Reading
        /// <summary>
        /// Takes what came to 'readerId' (a block of type 'readerType', on the antenna 'reader': see <see cref="AntennaReg.Sync"/>)
        /// on 'channel' since its last read, at most 'maxMessages'. A reader reads a channel with one payload filter.
        /// </summary>
        public ErrorCode ReadMessages<T>(
            long readerId,
            IAIBlockType readerType,
            AntennaReg reader,
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
            if (maxMessages <= 0 || reader == null || !reader.Working)
                return reader == null || !reader.Working ? ErrorCode.RecipientNotValid : ErrorCode.TryAgainLater;

            var log = _channels[(int)channel];
            ulong nowMs = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow);
            ulong lifeMs = (ulong)_messageExpiration.TotalMilliseconds;
            ulong cutoff = nowMs > lifeMs ? nowMs - lifeMs : 0;
            int outOfRange = 0, taken = 0;
            lock (log.Lock)
            {
                long cursor;
                log.Cursors.TryGetValue(readerId, out cursor);
                // Early exits, before any work (no registration, no scan): the channel never had a message / nothing new since
                // this reader's last read / everything new has expired already
                if (log.LastSeq <= cursor) return ErrorCode.TryAgainLater;
                if (log.LastSentAt < cutoff)
                {
                    log.Cursors[readerId] = log.LastSeq;
                    return ErrorCode.TryAgainLater;
                }
                Relays.Register(reader);
                var items = log.Items;
                for (int i = FirstAfter(items, cursor); i < items.Count && taken < maxMessages; i++)
                {
                    var m = items[i];
                    cursor = m.Seq;
                    if (m.SentAt < cutoff || m.SenderId == readerId) continue;
                    if (m.RecipientBlockType != IAIBlockType.None && (m.RecipientBlockType & readerType) == 0) continue;
                    if (messageFilters != PayloadType.None && (m.PayloadType & messageFilters) == 0) continue;
                    if (!CanShare(m.SenderOwnerId, reader.OwnerId)) continue;
                    if (!InRange(m, reader)) { outOfRange++; continue; }
                    var payload = m.Payload as T;   // the sender's object, shared: read-only
                    if (payload == null)
                    {
                        Log.Warning("VNet: {0} #{1} from {2}: payload is {3}, not {4}: dropped", channel, m.MessageId, m.SenderId,
                                    m.Payload != null ? m.Payload.GetType().Name : "null", typeof(T).Name);
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
        /// Is the reader where the message went? Within the sender's radius (as sent: a radio is heard beyond its own
        /// range, so only the sender's counts - or both, for messages sent with enforceCommsRange), else if the relay
        /// network carries it.
        /// </summary>
        private bool InRange(TimestampedMessage m, AntennaReg reader)
        {
            if (m.SenderAntennaId == reader.AntennaId) return true;   // one antenna, several blocks
            double d2 = Vector3D.DistanceSquared(m.SenderPosition, reader.Pos);
            bool direct = d2 <= m.SenderRange * m.SenderRange;
            if (m.BothRanges)
            {
                if (direct && d2 <= reader.Range * reader.Range) return true;
                return Relays.Reaches(m.SenderGridId, reader.GridId, m.SenderOwnerId) && Relays.Reaches(reader.GridId, m.SenderGridId, reader.OwnerId);
            }
            // Simulate IGC: an antenna can RECEIVE even if its transmit range is lower, but what it sends doesn't get there
            if (direct) return true;
            // Relayed: the same base (connected grids), or other bases / drones in between (TODO/13)
            return Relays.Reaches(m.SenderGridId, reader.GridId, m.SenderOwnerId);
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

        #region Antennas
        /// <summary>A block's antenna is off / gone: out of the relay network until it registers again (with its next send / read).</summary>
        public void DeregisterAntenna(long antennaId)
        {
            Relays.Deregister(antennaId);
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
        /// and the relay graph's antennas. Expired messages are dropped first.
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
                            PayloadTypeValue = msg.PayloadTypeValue,
                            SenderAntennaId = msg.SenderAntennaId,
                            SenderPosition = Vector3DData.FromVector3D(msg.SenderPosition),
                            SenderRange = msg.SenderRange,
                            SenderGridId = msg.SenderGridId,
                            BothRanges = msg.BothRanges,
                            Deliveries = msg.Deliveries,
                        });
                    }
                }
            }
            if (Log.DebugEnabled) Log.Debug("VNet: snapshot of {0} message(s), {1} antenna(s) for the save", saved.Count, Relays.Count);
            return MyUtilitiesDelegate.SerializeToBinary<MessageQueueSnapshot>(
                new MessageQueueSnapshot { Messages = saved, SavedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow), Antennas = Relays.Snapshot() });
        }

        /// <summary>
        /// World load (session start): the saved messages back in their channels, with the age they had at the save, and
        /// the relay graph's antennas (its world scan is due a minute later, not at once).
        /// </summary>
        public void Deserialize(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            var snapshot = MyUtilitiesDelegate.SerializeFromBinary<MessageQueueSnapshot>(data);
            if (snapshot == null) return;
            Relays.Restore(snapshot.Antennas, MyAPIGateway.Session.GameplayFrameCounter);
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
                    Log.Warning("VNet: saved message #{0} on {1} ({2}): payload could not be read, dropped", saved.MessageId, saved.Channel, (PayloadType)saved.PayloadTypeValue);
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
                    PayloadTypeValue = saved.PayloadTypeValue,
                    SenderAntennaId = saved.SenderAntennaId,
                    SenderPosition = saved.SenderPosition.ToVector3D(),
                    SenderRange = saved.SenderRange,
                    SenderGridId = saved.SenderGridId,
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
            Log.Info("VNet: {0} message(s) restored from the save (times moved on {1:F0} s)", restored, shift / 1000.0);
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
                    default: return null;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("VNet: payload {0} could not be decoded: {1}", type, ex.Message);
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
