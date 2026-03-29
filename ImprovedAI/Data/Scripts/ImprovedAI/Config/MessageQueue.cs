using ImprovedAI.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImprovedAI.Config
{
    public interface IMessageQueueConfig
    {
        float NetworkUpdateRate();
        int SchedulerAntennaCacheUpdateIntervalTicks();
        int SchedulerMessageThrottlingTicks();
        int SchedulerMessageReadLimit();
        int DroneMessageThrottlingTicks();
        int MessageRetentionTicks();
        int DlqMessageRetentionTicks();
        int MessageCleanupIntervalTicks();
        MessageSerializationMode SerializationMode();

    }
    /// <summary>
    /// This section is about the IAI network communication.
    /// System implements a pseudo-messaging system between scheduler, drones, and logistic computers.
    /// </summary>
    public class MessageQueueConfig : IMessageQueueConfig
    {
        public float networkUpdateRate { get; internal set; } = 10.0f;
        public float NetworkUpdateRate() => networkUpdateRate;
        /// <summary>
        /// Schedulers keep a cache of antennas. Limit how often said cache is updated.
        /// </summary>
        public int schedulerAntennaCacheUpdateIntervalTicks { get; internal set; } = 60;
        public int SchedulerAntennaCacheUpdateIntervalTicks() => schedulerAntennaCacheUpdateIntervalTicks;
        /// <summary>
        /// Throttling for scheduler reading subscribed messages.
        /// </summary>
        // 4 messages/second, scheduler expects a lot of messages from managed drones
        public int schedulerMessageThrottlingTicks { get; internal set; } = 15;
        public int SchedulerMessageThrottlingTicks() => schedulerMessageThrottlingTicks;
        public int schedulerMessageReadLimit { get; internal set; } = 50;
        public int SchedulerMessageReadLimit() => schedulerMessageReadLimit;
        /// <summary>
        /// Throttling for drone reading subscribed messages.
        /// </summary>
        // 1 message every 3 seconds, drone orders are not as frequent.
        public int droneMessageThrottlingTicks { get; internal set; } = 180; // 3 seconds
        public int DroneMessageThrottlingTicks() => droneMessageThrottlingTicks;
        /// <summary>
        /// How long messages live in the message queue.
        /// </summary>
        public int messageRetentionTicks { get; internal set; } = 1800; // 30 seconds
        public int MessageRetentionTicks() => messageRetentionTicks;
        /// <summary>
        /// If a message expires in the queue, it will be sent back to the sender on this long-lived queue.
        /// </summary>
        public int dlqMessageRetentionTicks { get; internal set; } = 36000; // 10 minutes
        public int DlqMessageRetentionTicks() => dlqMessageRetentionTicks;
        public int messageCleanupIntervalTicks { get; internal set; } = 3600; // 1 min
        public int MessageCleanupIntervalTicks() => messageCleanupIntervalTicks;
        public MessageSerializationMode messageSerializationMode { get; internal set; } = MessageSerializationMode.ProtoBuf;
        public MessageSerializationMode SerializationMode() => messageSerializationMode;

        public int maxBidsPerTask { get; internal set; } = 10;
        public int MaxBidsPerTask() => maxBidsPerTask;
    }
}
