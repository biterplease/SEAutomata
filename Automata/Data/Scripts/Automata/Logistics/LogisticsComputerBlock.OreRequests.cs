using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRage.Utils;

namespace Automata.Logistics
{
    /// <summary>
    /// "Ore requests mine" (2026-11-07, off by default), absolute either way. Ticked: ores under their Min are mined for
    /// this computer, never hauled - a Mining Surveyor it reaches through the network (same grid group, antennas) reads
    /// the shortfall (OreRequests: one per network and ore, every step of its own) and issues MineOre jobs on deposits it knows, unloaded here
    /// when this computer bids room on them; no surveyor in reach (or none answering, no deposit known, stone): no ore.
    /// Unticked: ores are hauled like any item, never mined for it (no other computer has them to spare: no ore).
    /// </summary>
    public partial class LogisticsComputerBlock
    {

        /// <summary>On, requesting (Request below Min), working.</summary>
        public bool OreRequestsMine
        {
            get { return settings != null && settings.Enabled && settings.OreRequestsMine && Has(OperationMode.Request) && block != null && block.IsWorking; }
        }

        public long EntityId { get { return entityId; } }
        public IMyCubeGrid Grid { get { return block != null ? block.CubeGrid : null; } }
        public long OwnerId { get { return block != null ? block.OwnerId : 0; } }

        /// <summary>One network's request for one ore: kg short, as of the last quota check.</summary>
        public struct OreRequest
        {
            public int Network;   // the CN index of the network short of it (the main network's too)
            public MyStringHash Ore;
            public int Kg;
        }

        /// <summary>Its ore requests, one per network and ore (OreRequestsMine on): each is its own order at a surveyor.</summary>
        public void OreRequests(List<OreRequest> into)
        {
            into.Clear();
            if (!OreRequestsMine) return;
            int main = IndexOfNetwork(primary);
            if (main > 0) foreach (var kv in mainSet.OreShort) into.Add(new OreRequest { Network = main, Ore = kv.Key, Kg = kv.Value });
            foreach (var s in netSets.Values)
                foreach (var kv in s.OreShort) into.Add(new OreRequest { Network = s.Index, Ore = kv.Key, Kg = kv.Value });
        }

        // A delivery for this computer's ore request: to the network that asked ('net', a CN index), else (an older job)
        // to one whose list is short of it
        private bool ShortOf(ConveyorNetwork network, Automata.Inventory.DiscreteInventory incoming, int net)
        {
            if (net > 0) return IndexOfNetwork(network) == net;
            var s = SetFor(network);
            if (s == null) return false;
            foreach (var item in incoming.GetAllItems()) if (s.OreShort.ContainsKey(item.Key)) return true;
            return false;
        }

        private static bool IsOreKey(MyStringHash key)
        {
            string s = key.String;
            return s != null && s.StartsWith("Ore/", StringComparison.Ordinal);
        }

        public bool Terminal_OreRequestsMine
        {
            get { return settings.OreRequestsMine; }
            set { if (settings.OreRequestsMine == value) return; settings.OreRequestsMine = value; SettingsEdited(); }
        }
    }
}
