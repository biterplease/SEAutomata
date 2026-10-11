using Automata.VirtualNetwork;

namespace Automata.Util
{
    public static class MessageUtil
    {
        public static string TopicToString(Channel topic)
        {
            switch (topic)
            {
            case Channel.LOGISTIC_REQUEST:
                return "LOGISTIC_REQUEST";
            case Channel.LOGISTIC_PUSH:
                return "LOGISTIC_PUSH";
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
