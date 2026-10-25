using ProtoBuf;

namespace Automata.Designator
{
    /// <summary>Player settings of a target designator block (terminal), saved in its save struct and synced.</summary>
    [ProtoContract]
    public class TargetDesignatorSettings
    {
        /// <summary>LCDs whose name contains this (AutomataLog app) show the designator orders; its own screen always does.</summary>
        [ProtoMember(1)] public string LcdTag = "[Designator]";
        /// <summary>The camera the "Designator:" toolbar actions point with (on this block's mechanical group); 0: none.</summary>
        [ProtoMember(2)] public long CameraId;
        /// <summary>The name its logs (and other blocks' logs) show; empty: the block's name.</summary>
        [ProtoMember(3)] public string Nickname = "";
        [ProtoMember(4)] public Util.LcdStyle Lcd = new Util.LcdStyle();
    }

    /// <summary>A target designator's save struct (<see cref="Util.BlockSave"/>; it has no runtime state worth saving).</summary>
    [ProtoContract]
    public class TargetDesignatorSaveData
    {
        [ProtoMember(1)] public TargetDesignatorSettings Settings;
        [ProtoMember(2)] public long EntityId;
    }
}
