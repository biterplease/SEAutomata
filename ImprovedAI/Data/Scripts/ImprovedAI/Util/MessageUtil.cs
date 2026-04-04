using ImprovedAI.VirtualNetwork;

namespace ImprovedAI.Util
{
    public static class MessageUtil
    {
        public static string TopicToString(Channel topic)
        {
            switch (topic)
            {
            case Channel.DRONE_REGISTRATION:
                return "DRONE_REGISTRATION";
            case Channel.DRONE_REPORTS:
                return "DRONE_REPORTS";
            case Channel.DRONE_PERFORMANCE:
                return "DRONE_PERFORMANCE";
            case Channel.DRONE_TASK_ANNOUNCEMENT:
                return "DRONE_TASK_ANNOUNCEMENT";
            case Channel.DRONE_TASK_ASSIGNMENT:
                return "DRONE_TASK_ASSIGNMENT";
            case Channel.LOGISTIC_REGISTRATION:
                return "LOGISTIC_REGISTRATION";
            case Channel.LOGISTIC_UPDATE:
                return "LOGISTIC_UPDATE";
            case Channel.LOGISTIC_REQUEST:
                return "LOGISTIC_REQUEST";
            case Channel.LOGISTIC_PUSH:
                return "LOGISTIC_PUSH";
            case Channel.SCHEDULER_FORWARD:
                return "SCHEDULER_FORWARD";
            case Channel.MAILMAN_FORWARD:
                return "MAILMAN_FORWARD";
            case Channel.DIRECT_MESSAGE:
                return "DIRECT_MESSAGE";
            case Channel.DEAD_LETTER_QUEUE:
                return "DEAD_LETTER_QUEUE";
            default:
                return topic.ToString();
            }
        }
    }
}
