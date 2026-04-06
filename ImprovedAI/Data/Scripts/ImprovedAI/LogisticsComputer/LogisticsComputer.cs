using ImprovedAI.Util;
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
            /// <summary>
            /// Provide inventory only for logistics tasks.
            /// </summary>
            ProvideForLogistics = 1,
            /// <summary>
            /// Provide inventory only for construction tasks.
            /// </summary>
            ProvideForConstruction = 2,
            /// <summary>
            /// Provide inventory for both construction and logistics.
            /// </summary>
            ProvideForConstructionAndLogistics = 4,
            /// <summary>
            /// Will only push inventory to the Logistic Network. If an item quota is set, it will respect it. Will not provide inventory for any tasks.
            /// </summary>
            PushOnly = 8,
            /// <summary>
            /// Will only request inventory from the Logistic Network. If an item quota is set, it will respect it. Will not provide inventory for any tasks.
            /// </summary>
            RequestOnly = 16
        }
        [ProtoMember(1)]
        public long EntityId;
        [ProtoMember(2)]
        public OperationMode _OperationMode;
        /// <summary>
        /// Positions of available connectors.
        /// </summary>
        [ProtoIgnore]
        public List<MatrixD> connectors;
        [ProtoMember(3)]
        public List<MatrixDData> storedConnectors;
        [ProtoMember(4)]
        public Inventory LastKnownInventory;
    }
}
