using ProtoBuf;

namespace Automata.Orchestrator
{
    /// <summary>Player settings of an orchestrator (terminal), saved in its save struct and synced.</summary>
    [ProtoContract]
    public class OrchestratorBlockSettings
    {
        /// <summary>LCDs whose name contains this show the orchestrator's status.</summary>
        [ProtoMember(1)] public string LcdTag = "[Orchestrator]";

        /// <summary>"Use parking protocol" (TODO/12): drones of this network without a home connector park as ParkingMode says
        /// (then, where that has no room: any friendly grid, then the ground). Off: they park on any friendly grid.</summary>
        [ProtoMember(2)] public bool StagingArea;
        /// <summary>Area size, in blocks of the orchestrator's grid (older saves: metres, converted once: AreaInBlocks), in
        /// the orchestrator block's axes: X right, Y up, Z forward. 50 m at most each way.</summary>
        [ProtoMember(3)] public float StagingSizeX = 20;
        [ProtoMember(4)] public float StagingSizeY = 5;
        [ProtoMember(5)] public float StagingSizeZ = 20;
        /// <summary>Area centre offset from the block, in blocks, same axes.</summary>
        [ProtoMember(6)] public float StagingOffsetX;
        [ProtoMember(7)] public float StagingOffsetY;
        [ProtoMember(8)] public float StagingOffsetZ;
        /// <summary>A few events worth a chat line go out through a Broadcast Controller on the construct (Util/BroadcastControllerSender).</summary>
        [ProtoMember(9)] public bool UseBroadcastController;
        /// <summary>With the parking protocol: the designated area (the box above), anywhere on the orchestrator's grid, or voxel.</summary>
        [ProtoMember(10)] public ParkingProtocolMode ParkingMode = ParkingProtocolMode.ParkingArea;
        /// <summary>The area's size / offset are in blocks (false: an older save in metres, converted on first use).</summary>
        [ProtoMember(11)] public bool AreaInBlocks;
        /// <summary>The name its logs (and other blocks' logs) show; empty: the block's name.</summary>
        [ProtoMember(12)] public string Nickname = "";
        [ProtoMember(13)] public Util.LcdStyle Lcd = new Util.LcdStyle();

        // Flight lanes (TODO/20, section 6). Written by the server: the network's lanes ("number|text") and the geometry of
        // the lane pair a player asked to see. Written by a player: one command at a time ("id|delete|7", resume, clear, draw).
        [ProtoMember(14)] public System.Collections.Generic.List<string> Lanes = new System.Collections.Generic.List<string>();
        [ProtoMember(15)] public string LaneCommand = "";
        [ProtoMember(16)] public int LaneDrawNumber;
        [ProtoMember(17)] public System.Collections.Generic.List<double> LaneDrawAB = new System.Collections.Generic.List<double>();
        [ProtoMember(18)] public System.Collections.Generic.List<double> LaneDrawBA = new System.Collections.Generic.List<double>();
        [ProtoMember(19)] public bool LaneDrawPending;
        [ProtoMember(20)] public string LaneDrawNameAB = "";
        [ProtoMember(21)] public string LaneDrawNameBA = "";
        /// <summary>The camera the "Designator:" toolbar actions point with (on this orchestrator's mechanical group); 0: none.</summary>
        [ProtoMember(22)] public long DesignatorCameraId;
        /// <summary>LCDs whose name contains this (AutomataLog app) show the designator orders (orchestrator's LCD style).</summary>
        [ProtoMember(23)] public string DesignatorLcdTag = "[Designator]";
    }

    /// <summary>Where the parking protocol puts drones: its area first, or the orchestrator's grid. Values are saved.</summary>
    public enum ParkingProtocolMode : byte
    {
        ParkingArea = 0,
        ParkOnGrid = 1,
        /// <summary>On voxel near the orchestrator (ground, cliff, cave, asteroid), grids avoided.</summary>
        Voxel = 2,
    }
}
