namespace Automata.Config
{
    public interface IMessageQueueConfig
    {
        int MessageRetentionTicks();
        int MessageCleanupIntervalTicks();

    }
    /// <summary>
    /// This section is about the Automata network communication.
    /// System implements a pseudo-messaging system between scheduler, drones, and logistic computers.
    /// </summary>
    public class MessageQueueConfig : IMessageQueueConfig
    {
        /// <summary>
        /// How long a message lives: readers that look within this time (and in range) get it.
        /// </summary>
        public int messageRetentionTicks { get; internal set; } = 600; // 10 seconds
        public int MessageRetentionTicks() => messageRetentionTicks;
        public int messageCleanupIntervalTicks { get; internal set; } = 600; // 10 s
        public int MessageCleanupIntervalTicks() => messageCleanupIntervalTicks;
    }
}
