using static ImprovedAI.Scheduler;

namespace ImprovedAI.Util
{
    /// <summary>
    /// Derives capability requirements from scheduler task types for bidding announcements.
    /// </summary>
    public static class SchedulerTaskCapabilities
    {
        public static readonly TaskType LogisticsTaskMask =
            TaskType.PreciseDelivery |
            TaskType.PreciseFetch |
            TaskType.ScanDelivery |
            TaskType.ScanFetch |
            TaskType.ActiveProvide;

        public static bool IsLogisticsTaskType(TaskType taskType)
        {
            return (taskType & LogisticsTaskMask) != 0;
        }

        public static Drone.Capabilities RequiredCapabilitiesFor(TaskType taskType)
        {
            Drone.Capabilities caps = Drone.Capabilities.None;

            if ((taskType & (TaskType.PreciseWelding | TaskType.ScanWeld)) != 0)
                caps |= Drone.Capabilities.CanWeld;

            if ((taskType & (TaskType.PreciseGrinding | TaskType.ScanGrind)) != 0)
                caps |= Drone.Capabilities.CanGrind;

            if ((taskType & (TaskType.PreciseDrilling | TaskType.ScanDrill)) != 0)
                caps |= Drone.Capabilities.CanDrill;

            if ((taskType & TaskType.PreciseCargoAirdrop) != 0)
                caps |= Drone.Capabilities.CanAirDrop;

            if ((taskType & (TaskType.PreciseDelivery | TaskType.PreciseFetch |
                TaskType.ScanDelivery | TaskType.ScanFetch | TaskType.ActiveProvide)) != 0)
                caps |= Drone.Capabilities.HasCargoContainers;

            if ((taskType & (TaskType.PreciseDrop | TaskType.PreciseCargoAirdrop |
                TaskType.MessengerPigeon)) != 0)
                caps |= Drone.Capabilities.HasSensors;

            if (caps == Drone.Capabilities.None && taskType != TaskType.None)
                caps |= Drone.Capabilities.HasCargoContainers;

            return caps;
        }
    }
}
