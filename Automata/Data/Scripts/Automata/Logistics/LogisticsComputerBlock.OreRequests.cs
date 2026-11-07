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
    /// the shortfall (OreShortfall, every step of its own) and issues MineOre jobs on deposits it knows, unloaded here
    /// when this computer bids room on them; no surveyor in reach (or none answering, no deposit known, stone): no ore.
    /// Unticked: ores are hauled like any item, never mined for it (no other computer has them to spare: no ore).
    /// </summary>
    public partial class LogisticsComputerBlock
    {
        private readonly Dictionary<MyStringHash, int> oreShortfall = new Dictionary<MyStringHash, int>(MyStringHash.Comparer);

        /// <summary>On, requesting (Request below Min), working.</summary>
        public bool OreRequestsMine
        {
            get { return settings != null && settings.Enabled && settings.OreRequestsMine && Has(OperationMode.Request) && block != null && block.IsWorking; }
        }

        public long EntityId { get { return entityId; } }
        public IMyCubeGrid Grid { get { return block != null ? block.CubeGrid : null; } }
        public long OwnerId { get { return block != null ? block.OwnerId : 0; } }

        /// <summary>Ores under their Min, kg short, as of the last quota check (OreRequestsMine on).</summary>
        public void OreShortfall(List<KeyValuePair<MyStringHash, int>> into)
        {
            into.Clear();
            if (!OreRequestsMine) return;
            foreach (var kv in oreShortfall) into.Add(kv);
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
