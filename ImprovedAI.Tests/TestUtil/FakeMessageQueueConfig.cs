using ImprovedAI.Config;

namespace ImprovedAI.Tests.TestUtil
{
    /// <summary>Mutable config for MessageQueue unit tests (main assembly exposes only internal setters).</summary>
    public sealed class FakeMessageQueueConfig : IMessageQueueConfig
    {
        public float networkUpdateRate { get; set; } = 10.0f;
        public float NetworkUpdateRate() => networkUpdateRate;
        public int schedulerAntennaCacheUpdateIntervalTicks { get; set; } = 60;
        public int SchedulerAntennaCacheUpdateIntervalTicks() => schedulerAntennaCacheUpdateIntervalTicks;
        public int schedulerMessageThrottlingTicks { get; set; } = 15;
        public int SchedulerMessageThrottlingTicks() => schedulerMessageThrottlingTicks;
        public int schedulerMessageReadLimit { get; set; } = 50;
        public int SchedulerMessageReadLimit() => schedulerMessageReadLimit;
        public int droneMessageThrottlingTicks { get; set; } = 180;
        public int DroneMessageThrottlingTicks() => droneMessageThrottlingTicks;
        public int messageRetentionTicks { get; set; } = 1800;
        public int MessageRetentionTicks() => messageRetentionTicks;
        public int dlqMessageRetentionTicks { get; set; } = 36000;
        public int DlqMessageRetentionTicks() => dlqMessageRetentionTicks;
        public int messageCleanupIntervalTicks { get; set; } = 3600;
        public int MessageCleanupIntervalTicks() => messageCleanupIntervalTicks;
        public MessageSerializationMode messageSerializationMode { get; set; } = MessageSerializationMode.ProtoBuf;
        public MessageSerializationMode SerializationMode() => messageSerializationMode;
    }
}
