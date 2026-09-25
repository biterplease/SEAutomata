using ProtoBuf;
using System;

using VRage.Utils;

namespace Automata.VirtualInventory
{
    /// <summary>
    /// Item subtype key as <see cref="MyStringHash"/> for efficient compare and network/proto (serializes <c>m_hash</c>).
    /// Use <see cref="MyStringHash.GetOrCompute"/> when creating keys from string subtype ids per VRage guidance.
    /// </summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public struct KVPair
    {
        [ProtoMember(1)]
        public MyStringHash Key;
        [ProtoMember(2)]
        public int Value;
    }

    [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
    public struct QuotaSet
    {
        [ProtoMember(1)]
        public MyStringHash Key;
        [ProtoMember(2)]
        public int MinimumValue;
        [ProtoMember(3)]
        public int MaximumValue;
    }
}