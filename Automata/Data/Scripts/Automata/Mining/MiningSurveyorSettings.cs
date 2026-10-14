using System.Collections.Generic;

using ProtoBuf;

namespace Automata.Mining
{
    /// <summary>Player settings of a Mining Surveyor (terminal), saved in the block's mod storage and synced.</summary>
    [ProtoContract]
    public class MiningSurveyorSettings
    {
        [ProtoMember(1, IsRequired = true)] public bool Enabled = true;
        /// <summary>Ore orders, "ore:kg" (ore = ore inventory key, e.g. "Ore/Iron:100000"). See <see cref="OreOrder"/>.</summary>
        [ProtoMember(2)] public List<string> Orders = new List<string>();
        /// <summary>Orders are not counted down: jobs go out while deposits of the ore are known.</summary>
        [ProtoMember(3, IsRequired = true)] public bool KeepOrdersConstant;
        /// <summary>Known deposits shown to the owner as temporary GPS markers.</summary>
        [ProtoMember(4, IsRequired = true)] public bool ShowGps;
        /// <summary>kg of ore one MineOre job asks for (one drone trip fills its cargo; the rest goes to the next job).</summary>
        [ProtoMember(5, IsRequired = true)] public int JobSizeKg = 10000;
        /// <summary>
        /// Written by the server only: the ores of known deposits and how many ("Ore/Iron:12"), so every player's
        /// terminal can list them. Replaced by the server whenever it changes.
        /// </summary>
        [ProtoMember(6)] public List<string> KnownOres = new List<string>();
        /// <summary>LCDs whose name contains this show the surveyor's status.</summary>
        [ProtoMember(7)] public string LcdTag = "[MiningSurveyor]";
        /// <summary>A few events worth a chat line go out through a Broadcast Controller on the construct (Util/BroadcastControllerSender).</summary>
        [ProtoMember(8)] public bool UseBroadcastController;
        /// <summary>"Retry jobs" (terminal): bumped by the button, the server acts on a change.</summary>
        [ProtoMember(10)] public int RetryRequest;
        /// <summary>"Show known deposits" (terminal): bumped by the button, the server acts on a change.</summary>
        [ProtoMember(11)] public int ShowDepositsRequest;
        /// <summary>"Delete known deposits" (terminal): bumped by the button, the server acts on a change.</summary>
        [ProtoMember(14)] public int DeleteDepositsRequest;
        /// <summary>The name its logs (and other blocks' logs) show; empty: the block's name.</summary>
        [ProtoMember(12)] public string Nickname = "";
        [ProtoMember(13)] public Util.LcdStyle Lcd = new Util.LcdStyle();
        /// <summary>Stone (Ore/Stone) is no ore worth a job: left out of the known ores, no orders or jobs for it. On by default.</summary>
        [ProtoMember(9, IsRequired = true)] public bool IgnoreStone = true;
    }
}
