using ProtoBuf;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

using Automata.Util;


namespace Automata.ConstructionComputer
{
    [Serializable, ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public class ConstructionComputerSettings
    {
        [ProtoMember(1)]
        public Vector3Data WeldIgnoreColor;
        [ProtoMember(2)]
        public Vector3Data GrindColor;
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
        public int ScanRetryIntervalSeconds = 600;
        [ProtoMember(7)]
        public int MaxJobAnnouncementsPerUpdate = 10;
        [ProtoMember(8)]
        public byte PerScanLimits = 100;
        [ProtoMember(9)]
        public WorkModes WorkModes;
        [ProtoMember(10)]
        public ShareWith ShareWith;
        [ProtoMember(11)]
        public OperationMode OperationMode;
        [ProtoMember(12)]
        public bool IgnoreTasksOutsideSpecifiedRange = false;
        [ProtoMember(13)]
        public bool IgnoreTasksOutsideOfAntenaRange = true;
        [ProtoMember(14)]
        public bool IsEnabled;
    }
}