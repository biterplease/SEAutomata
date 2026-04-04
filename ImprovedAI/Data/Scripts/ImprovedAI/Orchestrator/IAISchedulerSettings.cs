using ProtoBuf;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;
using static ImprovedAI.Scheduler;

namespace ImprovedAI
{
    [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public class IAISchedulerSettings
    {
        [ProtoMember(1)]
        public Vector3 WeldIgnoreColor;
        [ProtoMember(2)]
        public Vector3 GrindColor;
        /// <summary>
        /// Do not weld these block types.
        /// </summary>
        [ProtoMember(3)]
        public List<ulong> WeldIgnoreList;
        /// <summary>
        /// Do not grind these block types.
        /// </summary>
        [ProtoMember(4)]
        public List<ulong> GrindIgnoreList;
        /// <summary>
        /// Which tasks is this scheduler allowed to distribute.
        /// </summary>
        [ProtoMember(5)]
        public float IgnoreTasksOutsideSpecifiedRangeMeters = 1000.0f;
        [ProtoMember(6)]
        public WorkModes WorkModes;
        [ProtoMember(7)]
        public ShareWith ShareWith;
        [ProtoMember(8)]
        public OperationMode OperationMode;
        [ProtoMember(9)]
        public bool IgnoreTasksOutsideSpecifiedRange = false;
        [ProtoMember(10)]
        public bool IgnoreTasksOutsideOfAntenaRange = true;
        [ProtoMember(11)]
        public bool IsEnabled;
    }
}
