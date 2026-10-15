namespace Automata.Config
{
    public interface IMessageQueueConfig
    {
        float NetworkUpdateRate();
        int SchedulerAntennaCacheUpdateIntervalTicks();
        int SchedulerMessageReadLimit();
        int MessageRetentionTicks();
        int DlqMessageRetentionTicks();
        int MessageCleanupIntervalTicks();

    }
    /// <summary>
    /// This section is about the Automata network communication.
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
        public int schedulerMessageReadLimit { get; internal set; } = 50;
        public int SchedulerMessageReadLimit() => schedulerMessageReadLimit;
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

        public int maxBidsPerTask { get; internal set; } = 10;
        public int MaxBidsPerTask() => maxBidsPerTask;
    }
}
