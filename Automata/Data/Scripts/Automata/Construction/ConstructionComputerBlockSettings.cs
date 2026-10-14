using ProtoBuf;

namespace Automata.Construction
{
    /// <summary>
    /// Player settings of a Construction Computer block (terminal), saved in the block's mod storage and synced.
    /// </summary>
    [ProtoContract]
    public class ConstructionComputerBlockSettings
    {
        [ProtoMember(1, IsRequired = true)] public bool Enabled = true;
        /// <summary>What it looks for: unfinished / damaged / projected blocks to weld, blocks painted the grind color.</summary>
        [ProtoMember(2, IsRequired = true)] public WorkModes WorkModes = WorkModes.WeldUnfinishedBlocks | WorkModes.RepairDamagedBlocks | WorkModes.WeldProjectedBlocks;
        /// <summary>Seconds between scans.</summary>
        [ProtoMember(6)] public int ScanIntervalSeconds = 10;
        /// <summary>Blocks per announced job (an orchestrator splits a job into trips by drone capacity).</summary>
        [ProtoMember(7)] public int BlocksPerJob = 50;
        /// <summary>Blocks painted exactly this color are ground (Grind mode). Packed (Color.PackedValue).</summary>
        [ProtoMember(8)] public uint GrindColor = 0xFF0000FF;   // RGB(255,0,0)
        /// <summary>Blocks painted exactly this color are never welded, repaired or built (projections too).</summary>
        [ProtoMember(17)] public uint WeldIgnoreColor = 0xFFFF00FF;   // RGB(255,0,255)
        /// <summary>
        /// Observation area, in blocks of 2.5 m along the block's axes (right, up, forward): 1-50, default 25.
        /// </summary>
        [ProtoMember(9)] public int AreaSizeX = 25;
        [ProtoMember(10)] public int AreaSizeY = 25;
        [ProtoMember(11)] public int AreaSizeZ = 25;
        /// <summary>Retired (metres): read once and converted to the block offsets below (OffsetInBlocks).</summary>
        [ProtoMember(12)] public float AreaOffsetX;
        [ProtoMember(13)] public float AreaOffsetY;
        [ProtoMember(14)] public float AreaOffsetZ;
        /// <summary>Where the area's centre is from the block, in blocks of 2.5 m along its right, up, forward axes: -100 to 100.</summary>
        [ProtoMember(18)] public int AreaOffsetBlocksX;
        [ProtoMember(19)] public int AreaOffsetBlocksY;
        [ProtoMember(20)] public int AreaOffsetBlocksZ;
        /// <summary>The metre offsets were converted (older saves: false until loaded once).</summary>
        [ProtoMember(21)] public bool OffsetInBlocks;
        /// <summary>"Retry jobs" (terminal): bumped by the button, the server acts on a change.</summary>
        [ProtoMember(22)] public int RetryRequest;
        /// <summary>The order the weld categories are scanned and announced in (default: repair, projected, unbuilt).</summary>
        [ProtoMember(23)] public WorkPriority WorkPriority = WorkPriority.RepairProjectedUnbuilt;
        /// <summary>The name its logs (and other blocks' logs) show; empty: the block's name.</summary>
        [ProtoMember(24)] public string Nickname = "";
        [ProtoMember(25)] public Util.LcdStyle Lcd = new Util.LcdStyle();
        /// <summary>LCDs whose name contains this show the computer's status.</summary>
        [ProtoMember(15)] public string LcdTag = "[ConstructionComputer]";
        /// <summary>A few events worth a chat line go out through a Broadcast Controller on the construct (Util/BroadcastControllerSender).</summary>
        [ProtoMember(16)] public bool UseBroadcastController;
    }
}
