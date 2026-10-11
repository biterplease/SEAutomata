using System;
using System.Collections.Generic;

using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Util;
using VRage.Utils;

namespace Automata.Logistics
{
    /// <summary>A connector a drone can dock at: where, and which way it faces relative to gravity.</summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    public class DockPort
    {
        [ProtoMember(1)] public long EntityId;
        [ProtoMember(2)] public Vector3DData Position;
        [ProtoMember(3)] public QuaternionDData Orientation;
        /// <summary>Base6Directions.Direction, in the gravity frame: Up (faces the sky), Down (faces the ground) or
        /// Forward (sideways). Forward when there is no natural gravity.</summary>
        [ProtoMember(4)] public byte Facing;
        [ProtoMember(5)] public bool Occupied;
        /// <summary>A small connector: only a drone's small connector mates with it (TODO/14).</summary>
        [ProtoMember(6)] public bool Small;

        public IOLocationData ToIO()
        {
            return new IOLocationData { PositionData = Position, OrientationData = Orientation, BlockType = IOBlockType.ShipConnector, EntityId = EntityId };
        }
    }

    /// <summary>The connectors a logistics computer offers for a job (one conveyor network), with the gravity there.</summary>
    [Serializable, ProtoContract(UseProtoMembersOnly = true)]
    public class DockSite
    {
        [ProtoMember(1)] public List<DockPort> Ports;
        [ProtoMember(2)] public Vector3DData Gravity;
        /// <summary>Mobile bases: the beacon the network is relative to (0: none).</summary>
        [ProtoMember(3)] public long BeaconEntityId;

        public bool InGravity { get { return Gravity.ToVector3D().LengthSquared() > DockFit.GRAVITY_MIN * DockFit.GRAVITY_MIN; } }
    }

    /// <summary>
    /// Orientation-only docking compatibility (TODO/14). Drones fly the way their controller faces, and a drone built
    /// for gravity holds its weight with its downward thrust, so in natural gravity:
    ///  - a connector facing up (floor) needs a drone connector facing Down,
    ///  - a connector facing down (ceiling) needs one facing Up,
    ///  - a sideways connector (wall) needs one facing Forward, Backward, Left or Right (the drone yaws to it).
    /// Without natural gravity any drone connector fits any connector. Sizes always match: small with small, large with
    /// large.
    /// </summary>
    public static class DockFit
    {
        #region UI strings
        private static readonly MyStringId StrPortSSmallNoGravity = MyStringId.GetOrCompute("{0} port(s) ({1} small), no gravity");
        private static readonly MyStringId StrPortSSmallFacingUp = MyStringId.GetOrCompute("{0} port(s) ({4} small): {1} facing up, {2} down, {3} sideways");
        #endregion

        public const float GRAVITY_MIN = 0.1f;          // m/s²: below this, "no natural gravity"
        public const int MAX_PORTS = 16;
        private const double VERTICAL_COS = 0.7071;     // within 45° of vertical: up / down, else sideways

        public static byte FacingIn(Vector3D axis, Vector3D gravity)
        {
            if (gravity.LengthSquared() < GRAVITY_MIN * GRAVITY_MIN || axis.LengthSquared() < 1e-6) return (byte)Base6Directions.Direction.Forward;
            double up = Vector3D.Dot(Vector3D.Normalize(axis), -Vector3D.Normalize(gravity));
            if (up > VERTICAL_COS) return (byte)Base6Directions.Direction.Up;
            if (up < -VERTICAL_COS) return (byte)Base6Directions.Direction.Down;
            return (byte)Base6Directions.Direction.Forward;
        }

        public static bool Fits(byte droneFace, byte portFacing, bool inGravity)
        {
            if (!inGravity) return true;
            var d = (Base6Directions.Direction)droneFace;
            switch ((Base6Directions.Direction)portFacing)
            {
                case Base6Directions.Direction.Up:   return d == Base6Directions.Direction.Down;
                case Base6Directions.Direction.Down: return d == Base6Directions.Direction.Up;
                default:                             return d != Base6Directions.Direction.Up && d != Base6Directions.Direction.Down;
            }
        }

        /// <summary>The port a drone with these connector faces (standard / small) can dock at: its size, facing fitting,
        /// free before occupied, then the nearest to 'near'. Null if none fits. No site / no faces: null.</summary>
        public static DockPort Pick(DockSite site, List<byte> droneFaces, List<byte> smallFaces, Vector3D near, long dockedAt = 0)
        {
            if (site == null || site.Ports == null || (droneFaces == null && smallFaces == null)) return null;
            bool g = site.InGravity;
            DockPort best = null;
            double bestScore = double.MaxValue;
            for (int i = 0; i < site.Ports.Count; i++)
            {
                var p = site.Ports[i];
                var faces = p.Small ? smallFaces : droneFaces;
                if (faces == null) continue;
                bool fits = false;
                for (int f = 0; f < faces.Count && !fits; f++) fits = Fits(faces[f], p.Facing, g);
                if (!fits) continue;
                // The connector the drone sits at is occupied by the drone itself: the best one (nothing to undock from)
                double score = p.EntityId == dockedAt && dockedAt != 0 ? -1 : Vector3D.DistanceSquared(near, p.Position.ToVector3D()) + (p.Occupied ? 1e12 : 0);
                if (score < bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        /// <summary>The site of a conveyor network: its working connectors, both sizes (free first, MAX_PORTS at most).</summary>
        public static DockSite SiteOf(ConveyorNetwork n, Vector3D near)
        {
            if (n == null || n.connectors == null) return null;
            var site = new DockSite { Ports = new List<DockPort>() };
            float interference;
            Vector3D g = MyAPIGateway.Physics.CalculateNaturalGravityAt(near, out interference);
            site.Gravity = Vector3DData.FromVector3D(g);
            site.BeaconEntityId = n.RelativeBeacon != null && !n.RelativeBeacon.Closed ? n.RelativeBeacon.EntityId : 0;
            // Free before occupied; within each, standard before small (MAX_PORTS at most)
            for (int pass = 0; pass < 4; pass++)
                for (int i = 0; i < n.connectors.Count && site.Ports.Count < MAX_PORTS; i++)
                {
                    var c = n.connectors[i];
                    if (c == null || c.Closed || !c.IsWorking || (pass < 2) == c.IsConnected) continue;
                    if ((pass % 2 == 0) != ConveyorNetwork.IsStandardConnector(c)) continue;
                    Vector3D point, axis;
                    Drone.DroneControllerBlock.ConnectorGeometry(c, out point, out axis);
                    site.Ports.Add(new DockPort
                    {
                        EntityId = c.EntityId,
                        Position = Vector3DData.FromVector3D(c.GetPosition()),
                        Orientation = QuaternionDData.FromQuaternionD(QuaternionD.CreateFromRotationMatrix(c.WorldMatrix)),
                        Facing = FacingIn(axis, g),
                        Occupied = c.IsConnected,
                        Small = !ConveyorNetwork.IsStandardConnector(c),
                    });
                }
            return site.Ports.Count > 0 ? site : null;
        }

        public static string FaceName(byte f)
        {
            return ((Base6Directions.Direction)f).ToString();
        }

        public static string Describe(DockSite site)
        {
            if (site == null || site.Ports == null) return "-";
            int up = 0, down = 0, side = 0, small = 0;
            for (int i = 0; i < site.Ports.Count; i++)
            {
                if (site.Ports[i].Small) small++;
                var f = (Base6Directions.Direction)site.Ports[i].Facing;
                if (f == Base6Directions.Direction.Up) up++; else if (f == Base6Directions.Direction.Down) down++; else side++;
            }
            if (!site.InGravity) return Loc.F(StrPortSSmallNoGravity, site.Ports.Count, small);
            return Loc.F(StrPortSSmallFacingUp, site.Ports.Count, up, down, side, small);
        }
    }
}
