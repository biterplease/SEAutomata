using ProtoBuf;
using System;
using System.Collections.Generic;
using VRageMath;

namespace ImprovedAI
{
    [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    public class LogisticsComputer
    {
        public enum State : byte
        {
            Initializing,
            Active,
            Error
        }
        [Flags]
        public enum OperationMode : byte
        {
            None = 0,
            ProvideForLogistics = 1,
            ProvideForConstruction = 2,
            Push = 4,
            Request = 8
        }
        [ProtoMember(1)]
        public long EntityId;
        [ProtoMember(2)]
        public OperationMode _OperationMode;
        /// <summary>
        /// Positions of available connectors.
        /// </summary>
        [ProtoMember(3)]
        public List<Vector3D> connectors;
        [ProtoMember(4)]
        public Inventory LastKnownInventory;
    }
}
