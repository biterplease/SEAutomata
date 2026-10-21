using System.Collections.Generic;

using ProtoBuf;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

using Automata.Util;

namespace Automata.VirtualNetwork
{
    [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public sealed class LaserAntennaData
    {
        [ProtoMember(1)] public Vector3DData Position;
        [ProtoMember(2)] public Vector3DData TargetCoordinates;
        [ProtoMember(3)] public string Name;
        [ProtoMember(4)] public double Range;
        /// <summary>Entity id of the laser antenna it is connected to (0: not reported).</summary>
        [ProtoMember(5)] public long LinkedId;
        /// <summary>Alive, powered and connected: only those count as a link of the network.</summary>
        [ProtoMember(6)] public bool Working;
        [ProtoMember(7)] public long OwnerId;
        public LaserAntennaData() { }
    }

    [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public sealed class RadioAntennaData
    {
        [ProtoMember(1)] public Vector3DData Position;
        [ProtoMember(2)] public string Name;
        [ProtoMember(3)] public double Range;
        /// <summary>Alive, powered and broadcasting.</summary>
        [ProtoMember(4)] public bool Working;
        [ProtoMember(5)] public long OwnerId;
        public RadioAntennaData() { }
    }

    /// <summary>
    /// What the virtual network knows of one grid (TODO/13): plain data, no block references. The relay graph keeps one
    /// per grid id and refreshes it itself from the grid's antennas (a block just asks for its grid's, see
    /// <see cref="Ensure"/>); blocks, messages and the graph all share that one object. A grid without antennas has an
    /// entry too (its blocks still talk to blocks of the same group). Saved with the message queue.
    /// </summary>
    [ProtoContract(SkipConstructor = true, UseProtoMembersOnly = true)]
    public sealed class GridAntennaReg
    {
        [ProtoMember(1)] public string GridName;
        [ProtoMember(2)] public long GridId;
        /// <summary>The owner of its working antenna with the largest range (else a laser's, else the grid's / a block's).</summary>
        [ProtoMember(3)] public long OwnerId;
        [ProtoMember(4)] public bool IsStatic;
        /// <summary>Some block asked for it (else the relay graph found the grid while scanning the world).</summary>
        [ProtoMember(5)] public bool Registered;
        /// <summary>Radio antenna entity id -> its data.</summary>
        [ProtoMember(6)] public Dictionary<long, RadioAntennaData> RadioAntennas = new Dictionary<long, RadioAntennaData>();
        /// <summary>Laser antenna entity id -> its data.</summary>
        [ProtoMember(7)] public Dictionary<long, LaserAntennaData> LaserAntennas = new Dictionary<long, LaserAntennaData>();

        /// <summary>Relay graph bookkeeping (its node's key), not saved.</summary>
        public long GroupKey;
        /// <summary>Hash of what decides the graph's edges (set of working antennas, ranges, links, owner, static), not saved.</summary>
        public int Sig;

        public GridAntennaReg() { }

        #region State
        public bool IsAnyRadioWorking()
        {
            foreach (var r in RadioAntennas.Values) if (r.Working) return true;
            return false;
        }

        public bool IsAnyLaserWorking()
        {
            foreach (var l in LaserAntennas.Values) if (l.Working) return true;
            return false;
        }

        /// <summary>The grid can be a node of the network: a working radio antenna or a connected laser antenna.</summary>
        public bool Working() { return IsAnyRadioWorking() || IsAnyLaserWorking(); }

        /// <summary>The working radio antenna with the largest range, null: none.</summary>
        public RadioAntennaData BestRadio()
        {
            RadioAntennaData best = null;
            foreach (var r in RadioAntennas.Values)
                if (r.Working && (best == null || r.Range > best.Range)) best = r;
            return best;
        }

        /// <summary>Where the grid is (its best radio, else a working laser): to tell whether it moved.</summary>
        public bool TryGetPosition(out Vector3DData position)
        {
            var radio = BestRadio();
            if (radio != null) { position = radio.Position; return true; }
            foreach (var l in LaserAntennas.Values)
                if (l.Working) { position = l.Position; return true; }
            position = default(Vector3DData);
            return false;
        }
        #endregion

        #region From blocks
        /// <summary>
        /// What a block calls every update (a dictionary lookup unless the entry is due): its grid's entry, kept current by
        /// the relay graph. False: no queue / no usable block.
        /// </summary>
        public static bool Ensure(MessageQueue queue, ref GridAntennaReg reg, IMyCubeBlock block)
        {
            if (queue == null || block == null || block.CubeGrid == null) { reg = null; return false; }
            reg = queue.Relays.Touch(block.CubeGrid, block.OwnerId);
            return reg != null;
        }

        private static readonly List<long> drop = new List<long>();

        /// <summary>
        /// Reads the grid's antennas (the lists are the grid's, found by walking its blocks). True: something that decides
        /// the graph's edges changed (an antenna on / off / added / gone, its range, a laser's link, owner, static).
        /// </summary>
        public bool Refresh(IMyCubeGrid grid, List<IMyRadioAntenna> radios, List<IMyLaserAntenna> lasers)
        {
            GridId = grid.EntityId;
            GridName = grid.DisplayName;
            IsStatic = grid.IsStatic;
            int sig = IsStatic ? 1 : 0;
            double bestRange = -1;
            long radioOwner = 0, laserOwner = 0;

            drop.Clear();
            foreach (var k in RadioAntennas.Keys) drop.Add(k);
            for (int i = 0; i < radios.Count; i++)
            {
                var a = radios[i];
                if (!AntennaUtil.Alive(a) || a.CubeGrid != grid) continue;
                RadioAntennaData d;
                if (!RadioAntennas.TryGetValue(a.EntityId, out d)) { d = new RadioAntennaData(); RadioAntennas[a.EntityId] = d; }
                drop.Remove(a.EntityId);
                d.Position = Vector3DData.FromVector3D(a.GetPosition());
                d.Name = AntennaUtil.Name(a);
                d.Range = AntennaUtil.Range(a);
                d.OwnerId = a.OwnerId;
                d.Working = a.OwnerId != 0 && AntennaUtil.AntennaReady(a);
                if (!d.Working) continue;
                unchecked { sig += (int)(a.EntityId * 2654435761L) ^ (int)d.Range; }
                if (d.Range > bestRange) { bestRange = d.Range; radioOwner = d.OwnerId; }
            }
            for (int i = 0; i < drop.Count; i++) RadioAntennas.Remove(drop[i]);

            drop.Clear();
            foreach (var k in LaserAntennas.Keys) drop.Add(k);
            for (int i = 0; i < lasers.Count; i++)
            {
                var l = lasers[i];
                if (!AntennaUtil.Alive(l) || l.CubeGrid != grid) continue;
                LaserAntennaData d;
                if (!LaserAntennas.TryGetValue(l.EntityId, out d)) { d = new LaserAntennaData(); LaserAntennas[l.EntityId] = d; }
                drop.Remove(l.EntityId);
                d.Position = Vector3DData.FromVector3D(l.GetPosition());
                d.TargetCoordinates = Vector3DData.FromVector3D(l.TargetCoords);
                var other = l.Other;
                d.LinkedId = other != null ? other.EntityId : 0;
                d.Name = AntennaUtil.Name(l);
                d.Range = AntennaUtil.Range(l);
                d.OwnerId = l.OwnerId;
                d.Working = l.OwnerId != 0 && AntennaUtil.AntennaReady(l);
                if (!d.Working) continue;
                unchecked { sig += (int)(l.EntityId * 40503L) ^ (int)(d.LinkedId * 31L); }
                if (laserOwner == 0) laserOwner = d.OwnerId;
            }
            for (int i = 0; i < drop.Count; i++) LaserAntennas.Remove(drop[i]);
            drop.Clear();

            long owner = radioOwner != 0 ? radioOwner : laserOwner;
            if (owner == 0) owner = OwnerId;
            if (owner == 0 && grid.BigOwners != null && grid.BigOwners.Count > 0) owner = grid.BigOwners[0];
            OwnerId = owner;
            unchecked { sig += (int)owner * 7; }
            bool changed = sig != Sig;
            Sig = sig;
            return changed;
        }
        #endregion
    }
}
