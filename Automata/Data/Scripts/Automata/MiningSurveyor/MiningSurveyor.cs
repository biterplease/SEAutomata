using ProtoBuf;
using System;
using VRageMath;

namespace Automata.MiningSurveyor
{
    [Flags, ProtoContract]
    public enum OperationMode : byte
    {
        [ProtoEnum]
        None = 0,
        /// <summary>
        /// Built into an orchestrator block; jobs are forwarded to the parent queue.
        /// </summary>
        [ProtoEnum]
        BuiltInToOrchestrator = 1,
        /// <summary>
        /// Standalone programmable block.
        /// </summary>
        [ProtoEnum]
        MiningSurveyor = 2,
    }

    [Flags, ProtoContract]
    public enum WorkModes : byte
    {
        [ProtoEnum]
        None = 0,
        /// <summary>
        /// Receive and index survey data only; do not enqueue MineOre job announcements.
        /// </summary>
        [ProtoEnum]
        ScanOnly = 1,
        /// <summary>
        /// Index data and publish MineOre jobs (subject to quotas and limits).
        /// </summary>
        [ProtoEnum]
        ScanAndPublish = 2,
    }
    /// <summary>
    /// Serialized footprint for the mining surveyor block (mirrors <see cref="ConstructionComputer"/> pattern).
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    public class MiningSurveyor
    {

        /// <summary>
        /// Half of the small-grid ore detector range (50 m): duplicate detections within this radius are discarded.
        /// </summary>
        public const double DuplicateDetectionRadiusMeters = 25.0;

        public static readonly double DuplicateDetectionRadiusSquared =
            DuplicateDetectionRadiusMeters * DuplicateDetectionRadiusMeters;

        [ProtoMember(1)]
        public long EntityId;
        [ProtoMember(2)]
        public OperationMode _OperationMode;
    }
}
