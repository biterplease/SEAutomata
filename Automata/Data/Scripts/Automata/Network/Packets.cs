using System.Collections.Generic;

using ProtoBuf;

using Automata.Drone;
using Automata.Util;
using Digi.NetworkLib;

namespace Digi.NetworkLib
{
    // Packet registry for Digi's NetworkLib (Network/NetworkLib is kept unmodified).
    // Tags must never be reused for a different packet type.
    [ProtoInclude(10, typeof(Automata.Network.DroneSettingPacket))]
    [ProtoInclude(11, typeof(Automata.Network.DroneActionPacket))]
    [ProtoInclude(12, typeof(Automata.Network.DroneLogPacket))]
    [ProtoInclude(13, typeof(Automata.Network.DroneAnchorListPacket))]
    [ProtoInclude(14, typeof(Automata.Network.DroneNavRoutePacket))]
    public abstract partial class PacketBase
    {
    }
}

namespace Automata.Network
{
    /// <summary>
    /// One drone setting changed. Client -> server (validated, applied, relayed to the other clients), or
    /// server -> clients (changes made on the server / by the host player).
    /// </summary>
    [ProtoContract]
    public class DroneSettingPacket : PacketBase
    {
        public DroneSettingPacket() { }   // required for deserialization

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public DroneSettingKey Key;
        [ProtoMember(3)] public double Number;
        [ProtoMember(4)] public string Text;
        [ProtoMember(5)] public Vector3IData VectorA;
        [ProtoMember(6)] public Vector3IData VectorB;
        [ProtoMember(7)] public long Id;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            var drone = DroneControllerBlock.Find(EntityId);
            if (drone != null) drone.ReceiveSetting(this, ref packetInfo, senderSteamId);
        }
    }

    /// <summary>
    /// A player asked the drone to do something. Always client -> server; the server checks access and executes.
    /// </summary>
    [ProtoContract]
    public class DroneActionPacket : PacketBase
    {
        public DroneActionPacket() { }

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public DroneAction Action;
        [ProtoMember(3)] public Vector3DData VectorA;
        [ProtoMember(4)] public Vector3DData VectorB;
        [ProtoMember(5)] public bool HasVectorA;
        [ProtoMember(6)] public bool HasVectorB;
        [ProtoMember(7)] public long Id;
        [ProtoMember(8)] public int Code;
        [ProtoMember(9)] public string Text;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            var drone = DroneControllerBlock.Find(EntityId);
            if (drone != null) drone.ReceiveAction(this, senderSteamId);
        }
    }

    /// <summary>
    /// A player-facing log line (terminal custom info), server -> clients.
    /// </summary>
    [ProtoContract]
    public class DroneLogPacket : PacketBase
    {
        public DroneLogPacket() { }

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public string Line;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            var drone = DroneControllerBlock.Find(EntityId);
            if (drone != null) drone.ReceiveLog(Line);
        }
    }

    /// <summary>
    /// Connector / beacon list for one drone, server -> the player who asked. Already filtered by the drone
    /// owner's AND that player's owner/faction rule.
    /// </summary>
    [ProtoContract]
    public class DroneAnchorListPacket : PacketBase
    {
        public DroneAnchorListPacket() { }

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public List<AnchorEntry> Entries;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            var drone = DroneControllerBlock.Find(EntityId);
            if (drone != null) drone.ReceiveAnchorList(Entries);
        }
    }

    /// <summary>
    /// Debug overlay: a drone's navigation targets (world, at send time), server -> a player who switched
    /// "Draw navigation targets" on (terminal access checked when subscribing).
    /// </summary>
    [ProtoContract]
    public class DroneNavRoutePacket : PacketBase
    {
        public DroneNavRoutePacket() { }

        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public List<Vector3DData> Points;
        /// <summary>Controller-local point the active order steers (tool / connector); the route starts there.</summary>
        [ProtoMember(3)] public Vector3DData ReferenceOffset;

        public override void Received(ref PacketInfo packetInfo, ulong senderSteamId)
        {
            var drone = DroneControllerBlock.Find(EntityId);
            if (drone != null) drone.ReceiveNavRoute(Points, ReferenceOffset);
        }
    }
}
