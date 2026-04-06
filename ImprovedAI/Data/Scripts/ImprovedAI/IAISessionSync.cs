using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImprovedAI
{
    [ProtoContract]
    public enum IAIEntityType: byte
    {
        [ProtoEnum]
        SchedulerBlock = 1,
        [ProtoEnum]
        DroneControllerBlock = 2,
        [ProtoEnum]
        LogisticsComputerBlock = 4,
        [ProtoEnum]
        ConstructionComputerBlock = 8,
    }
    [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public struct RegisterEntity
    {
        [ProtoMember(1)]
        public ulong SteamId;
        [ProtoMember(2)]
        public long EntityId;
        [ProtoMember(3)]
        public IAIEntityType EntityType;

    }
    public class IAISessionSync
    {
    }
}
