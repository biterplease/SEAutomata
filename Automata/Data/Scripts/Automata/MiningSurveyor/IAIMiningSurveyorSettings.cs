using ProtoBuf;
using System.Collections.Generic;
using VRage;

using Automata.Inventory;

namespace Automata.MiningSurveyor
{
    [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public class IAIMiningSurveyorSettings
    {
        /// <summary>
        /// Ore subtype names (e.g. Iron, Nickel) to ignore completely.
        /// </summary>
        [ProtoMember(1)]
        public List<string> OreIgnoreList;

        [ProtoMember(2)]
        public float IgnoreSamplesOutsideSpecifiedRangeMeters = 10000.0f;

        [ProtoMember(3)]
        public bool IgnoreSamplesOutsideSpecifiedRange = false;

        [ProtoMember(4)]
        public bool IgnoreSamplesOutsideOfAntennaRange = true;

        [ProtoMember(5)]
        public int MaxJobAnnouncementsPerUpdate = 10;

        [ProtoMember(6)]
        public WorkModes WorkModes;

        [ProtoMember(7)]
        public ShareWith ShareWith;

        [ProtoMember(8)]
        public OperationMode OperationMode;

        [ProtoMember(9)]
        public bool IsEnabled;

        /// <summary>
        /// When true, only ores listed with a positive request mass in <see cref="OreRequests"/> generate jobs.
        /// </summary>
        [ProtoMember(10)]
        public bool UseOreRequests;

        /// <summary>
        /// Target mass per ore subtype for scheduling (logistics-style quota intent).
        /// </summary>
        [ProtoMember(11)]
        public OreMassInventory OreRequests;

        /// <summary>
        /// When true, cap cumulative scheduled mass per subtype using <see cref="OreLimits"/>.
        /// </summary>
        [ProtoMember(12)]
        public bool UseOreLimits;

        [ProtoMember(13)]
        public OreMassInventory OreLimits;

        /// <summary>
        /// Nominal ore mass attributed to each published MineOre job for mass/volume accounting (game inventory uses MyFixedPoint).
        /// </summary>
        [ProtoMember(14)]
        public MyFixedPoint NominalMassPerMineOreJob;

        public IAIMiningSurveyorSettings()
        {
            OreIgnoreList = new List<string>();
            OreRequests = new OreMassInventory();
            OreLimits = new OreMassInventory();
            NominalMassPerMineOreJob = (MyFixedPoint)1000f;
        }
    }
}
