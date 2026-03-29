using ImprovedAI;
using System;
using System.IO;
using ProtoBuf;

namespace ImprovedAI.Tests.TestUtil
{
    /// <summary>
    /// Headless protobuf round-trip for MessageQueue tests (no MyAPIGateway).
    /// </summary>
    public sealed class ProtoBufMyUtilitiesDelegate : IMyUtilitiesDelegate
    {
        public byte[] SerializeToBinary<T>(T obj)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                Serializer.Serialize(ms, obj);
                return ms.ToArray();
            }
        }

        public T SerializeFromBinary<T>(byte[] bytes)
        {
            using (MemoryStream ms = new MemoryStream(bytes))
            {
                return Serializer.Deserialize<T>(ms);
            }
        }

        public string SerializeToXML<T>(T objectToSerialize)
        {
            throw new NotSupportedException("XML serialization not used in virtual network tests.");
        }

        public T SerializeFromXML<T>(string buffer)
        {
            throw new NotSupportedException("XML serialization not used in virtual network tests.");
        }
    }
}
