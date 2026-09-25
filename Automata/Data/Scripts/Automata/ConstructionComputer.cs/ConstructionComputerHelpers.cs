using ProtoBuf;
using System;

namespace Automata.ConstructionComputer
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
        BuiltInToOrchestrator = 1,
        /// <summary>
        /// Standard operatin mode.
        /// </summary>
        ConstructionComputer = 2,
        BuiltInToDrone = 4,
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
}
