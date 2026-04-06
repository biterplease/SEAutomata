using ImprovedAI.Util;
using ProtoBuf;
using System;
using System.Collections.Generic;
using VRageMath;


namespace ImprovedAI
{
    /// <summary>
    /// Handles construction task search and announcement.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    public class ConstructionComputer
    {
        public enum State : byte
        {
            Initializing,
            Standby,
            ScanningForJobs,
            PublishingJobs,
            Error
        }
        [Flags]
        public enum OperationMode : byte
        {
            None = 0,
            /// <summary>
            /// This is the mode used when its part of the Orchestrator.
            /// </summary>
            BuiltIn = 1,
            /// <summary>
            /// Standard operatin mode.
            /// </summary>
            ConstructionComputer = 2,
        }
        [Flags, ProtoContract]
        public enum SearchModes : byte
        {
            [ProtoEnum]
            ConnectedGrids = 1,
            [ProtoEnum]
            BoundingBox = 2
        }
        [Flags, ProtoContract]
        public enum WorkModes : byte
        {
            None = 0,
            ScanOnly = 1,
            WeldUnfinishedBlocks = 2,
            RepairDamagedBlocks = 4,
            WeldProjectedBlocks = 8,
            Grind = 16,
        }
        [ProtoMember(1)]
        public long EntityId;
        [ProtoMember(2)]
        public OperationMode _OperationMode;
    }
}
