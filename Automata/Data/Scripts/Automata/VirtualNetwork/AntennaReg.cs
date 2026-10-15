using ProtoBuf;
using Sandbox.ModAPI;
using VRageMath;

using Automata.Util;
using Automata.Util.Logging;

namespace Automata.VirtualNetwork
{
    public enum AntennaKind : byte
    {
        Radio = 0,
        Laser = 1,
    }

    /// <summary>
    /// What the virtual network knows of an antenna: plain data, no block reference (the block, if ever needed, is
    /// MyAPIGateway.Entities.TryGetEntityById(AntennaId)). Blocks build one for their antenna and hand it to the queue
    /// with every send / read; the queue passes it on to the relay graph, which keeps one entry per antenna id (an
    /// orchestrator, logistics computer, construction computer and mining surveyor on one base share theirs).
    /// Static grids' entries are never updated by blocks: they change only at the relay graph's fallback rebuild.
    /// Saved with the message queue (the relay graph is built from these).
    /// </summary>
    [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public sealed class AntennaReg
    {
        [ProtoMember(1)] public long AntennaId;
        [ProtoMember(2)] public AntennaKind Kind;
        [ProtoMember(3)] public Vector3DData Position;
        [ProtoMember(4)] public double Range;
        /// <summary>The antenna's grid is static (a station): nothing moves it, so blocks don't refresh the entry.</summary>
        [ProtoMember(5)] public bool GridIsStatic;
        [ProtoMember(6)] public long OwnerId;
        /// <summary>Entity id of the antenna's grid (the relay graph groups grids that are logically connected).</summary>
        [ProtoMember(7)] public long GridId;
        /// <summary>Radio: working and broadcasting. Laser: working and connected. Others are not part of the network.</summary>
        [ProtoMember(8)] public bool Working;
        /// <summary>Laser: entity id of the antenna it is connected to (0: not reported).</summary>
        [ProtoMember(9)] public long LinkedId;
        /// <summary>Laser: the target coordinates (used to match the other end when neither reports its link).</summary>
        [ProtoMember(10)] public Vector3DData Target;
        [ProtoMember(11)] public string Name;
        [ProtoMember(12)] public string GridName;
        /// <summary>Some block registered it (else the relay graph found it while scanning the world).</summary>
        [ProtoMember(13)] public bool Registered;

        /// <summary>Relay graph bookkeeping (logical group of the grid), not saved.</summary>
        public long GroupKey;

        public AntennaReg() { }

        public Vector3D Pos { get { return new Vector3D(Position.X, Position.Y, Position.Z); } }

        public AntennaReg Clone()
        {
            var c = new AntennaReg();
            c.CopyFrom(this);
            return c;
        }

        public void CopyFrom(AntennaReg o)
        {
            AntennaId = o.AntennaId; Kind = o.Kind; Position = o.Position; Range = o.Range; GridIsStatic = o.GridIsStatic;
            OwnerId = o.OwnerId; GridId = o.GridId; Working = o.Working; LinkedId = o.LinkedId; Target = o.Target;
            Name = o.Name; GridName = o.GridName; Registered = o.Registered;
        }

        #region From blocks
        public static AntennaReg Make(IMyRadioAntenna a)
        {
            var reg = new AntennaReg { AntennaId = a.EntityId, Kind = AntennaKind.Radio };
            reg.Name = a.CustomName;
            reg.Refresh(a);
            return reg;
        }

        public static AntennaReg Make(IMyLaserAntenna l)
        {
            var reg = new AntennaReg { AntennaId = l.EntityId, Kind = AntennaKind.Laser };
            reg.Name = l.CustomName;
            reg.Refresh(l);
            return reg;
        }

        /// <summary>
        /// The registration a block keeps for its antenna: made once, then refreshed only while the grid moves (a drone, a
        /// mobile base); a static grid's stays as it is. Call it before every send / read.
        /// </summary>
        public static AntennaReg Sync(AntennaReg reg, IMyRadioAntenna a)
        {
            if (reg == null || reg.AntennaId != a.EntityId) return Make(a);
            if (reg.GridIsStatic && a.CubeGrid != null && a.CubeGrid.IsStatic)
            {
                // Static grid: nothing moves, so only a switched antenna changes the entry. A grid that has turned mobile (IsStatic
                // false) falls through and is refreshed from now on.
                if (Live(a) == reg.Working) return reg;
            }
            reg.Refresh(a);
            return reg;
        }

        /// <summary>Alive, powered, and broadcasting.</summary>
        public static bool Live(IMyRadioAntenna a)
        {
            return AntennaUtil.Alive(a) && a.IsWorking && a.EnableBroadcasting;
        }

        /// <summary>
        /// What a block calls before using its antenna (every update, cheap): keeps its own registration current, tells the
        /// queue about a new / switched antenna, and says whether the antenna can be used. A lost antenna is taken out of
        /// the network.
        /// </summary>
        public static bool Ensure(MessageQueue queue, ref AntennaReg reg, IMyRadioAntenna a)
        {
            if (!AntennaUtil.Alive(a))
            {
                if (reg != null && queue != null) queue.DeregisterAntenna(reg.AntennaId);
                reg = null;
                return false;
            }
            bool first = reg == null || reg.AntennaId != a.EntityId;
            bool was = !first && reg.Working;
            reg = Sync(reg, a);
            // The queue is told when the antenna is new or switched; otherwise the entry is updated by the reg that goes with
            // each send / read (and by the queue's fallback rebuild), so nothing is paid here for a block with nothing to read
            if (queue != null && (first || was != reg.Working)) queue.RegisterAntenna(reg);
            if (first) Log.Info("VNet: antenna {0} '{1}' ({2:F0} m){3}", reg.AntennaId, reg.Name, reg.Range, reg.GridIsStatic ? ", static" : "");
            return Live(a);
        }

        public void Refresh(IMyRadioAntenna a)
        {
            var grid = a.CubeGrid;
            Position = Vector3DData.FromVector3D(a.GetPosition());
            Range = AntennaUtil.Range(a);
            Working = Live(a);
            OwnerId = a.OwnerId;
            GridIsStatic = grid != null && grid.IsStatic;
            if (grid != null) { GridId = grid.EntityId; GridName = grid.DisplayName; }
        }

        public void Refresh(IMyLaserAntenna l)
        {
            var grid = l.CubeGrid;
            Position = Vector3DData.FromVector3D(l.GetPosition());
            Range = 0;
            Working = !l.Closed && !l.MarkedForClose && l.IsWorking && l.Status == Sandbox.ModAPI.Ingame.MyLaserAntennaStatus.Connected;
            OwnerId = l.OwnerId;
            var other = l.Other;
            LinkedId = other != null ? other.EntityId : 0;
            Target = Vector3DData.FromVector3D(l.TargetCoords);
            GridIsStatic = grid != null && grid.IsStatic;
            if (grid != null) { GridId = grid.EntityId; GridName = grid.DisplayName; }
        }
        #endregion
    }
}
