using ProtoBuf;
using System;

namespace ImprovedAI
{
        [Serializable, ProtoContract(UseProtoMembersOnly = true, SkipConstructor = true)]
        public struct KVPair
        {
            [ProtoMember(1)]
            public string Key;
            [ProtoMember(2)]
            public int Value;
        }
}