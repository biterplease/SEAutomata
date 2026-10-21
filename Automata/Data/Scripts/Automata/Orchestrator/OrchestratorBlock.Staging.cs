using System;

using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Utils;
using VRageMath;

using BlendTypeEnum = VRageRender.MyBillboard.BlendTypeEnum;

namespace Automata.Orchestrator
{
    /// <summary>
    /// The parking protocol (TODO/12): drones of this network without a home connector park on their landing gears in
    /// the orchestrator's parking area (a box in the block's axes, sized / placed in blocks, 50 m at most each way) or
    /// anywhere on its grid. No slots: each drone finds its own free spot. Drones fall back area > the orchestrator's
    /// grid > any friendly grid > the ground (DroneControllerBlock.GoPark).
    /// </summary>
    public partial class OrchestratorBlock : IDebugDrawable
    {
        /// <summary>Area limits: size in blocks (at least one), 50 m at most each way; offset 250 m at most. The sliders span the
        /// small-grid range (100 / 500 blocks); a large grid is clamped to the metres.</summary>
        public const float STAGING_SIZE_MIN = 1, STAGING_SIZE_MAX_M = 50, STAGING_OFFSET_MAX_M = 250;
        public const float STAGING_SIZE_SLIDER_MAX = 100, STAGING_OFFSET_SLIDER_MAX = 500;
        private static readonly MyStringId StagingMaterial = MyStringId.GetOrCompute("Square");
        private bool stagingDraw;
        private int stagingDrawStart;

        /// <summary>The staging area in the world: box matrix (block axes, box centre) and half extents. False when off.</summary>
        public bool TryGetStagingArea(out MatrixD box, out Vector3D half)
        {
            box = MatrixD.Identity;
            half = Vector3D.Zero;
            if (block == null || settings == null || !settings.StagingArea || settings.ParkingMode != ParkingProtocolMode.ParkingArea) return false;
            EnsureAreaInBlocks();
            double gs = GridSize;
            MatrixD m = block.WorldMatrix;
            var offset = new Vector3D(settings.StagingOffsetX, settings.StagingOffsetY, -settings.StagingOffsetZ) * gs;   // +Z = forward
            box = m;
            box.Translation = Vector3D.Transform(offset, m);
            half = new Vector3D(SizeBlocks(settings.StagingSizeX), SizeBlocks(settings.StagingSizeY), SizeBlocks(settings.StagingSizeZ)) * gs * 0.5;
            return true;
        }

        /// <summary>The parking protocol is on (drones of this network park as it says).</summary>
        public bool ParkingProtocol { get { return settings != null && settings.StagingArea; } }
        /// <summary>The protocol's mode is the parking area (else: anywhere on the orchestrator's grid).</summary>
        public bool UsesParkingArea { get { return ParkingProtocol && settings.ParkingMode == ParkingProtocolMode.ParkingArea; } }
        /// <summary>The protocol's mode is voxel: drones park on voxel near the orchestrator, avoiding grids.</summary>
        public bool UsesVoxel { get { return ParkingProtocol && settings.ParkingMode == ParkingProtocolMode.Voxel; } }
        /// <summary>Where voxel parking looks from: the orchestrator block.</summary>
        public Vector3D ParkingCentre { get { return block != null ? block.GetPosition() : Vector3D.Zero; } }
        /// <summary>The grid drones park on with the protocol (and the fallback after the area).</summary>
        public IMyCubeGrid ParkingGrid { get { return block != null ? block.CubeGrid : null; } }

        private double GridSize { get { return block != null && block.CubeGrid != null ? block.CubeGrid.GridSize : 2.5; } }
        private float MaxSizeBlocks { get { return (float)Math.Max(STAGING_SIZE_MIN, Math.Floor(STAGING_SIZE_MAX_M / GridSize)); } }
        private float MaxOffsetBlocks { get { return (float)Math.Floor(STAGING_OFFSET_MAX_M / GridSize); } }

        private float SizeBlocks(float blocks)
        {
            return MathHelper.Clamp((float)Math.Round(blocks), STAGING_SIZE_MIN, MaxSizeBlocks);
        }

        // Older saves (and new blocks, whose defaults are metres): sizes / offsets converted to blocks, once
        private void EnsureAreaInBlocks()
        {
            if (settings == null || settings.AreaInBlocks || block == null || block.CubeGrid == null) return;
            double gs = GridSize;
            settings.StagingSizeX = SizeBlocks((float)(settings.StagingSizeX / gs));
            settings.StagingSizeY = SizeBlocks((float)(settings.StagingSizeY / gs));
            settings.StagingSizeZ = SizeBlocks((float)(settings.StagingSizeZ / gs));
            settings.StagingOffsetX = (float)Math.Round(settings.StagingOffsetX / gs);
            settings.StagingOffsetY = (float)Math.Round(settings.StagingOffsetY / gs);
            settings.StagingOffsetZ = (float)Math.Round(settings.StagingOffsetZ / gs);
            settings.AreaInBlocks = true;
        }

        /// <summary>
        /// The nearest staging area of an orchestrator of the drone's network (owner / faction), only where the
        /// orchestrator has it switched on (checkbox) and is running; null: none (the drone parks on a friendly grid).
        /// </summary>
        public static OrchestratorBlock StagingFor(IMyCubeGrid droneGrid, long droneOwner)
        {
            return StagingFor(droneGrid, droneOwner, droneGrid.GetPosition());
        }

        /// <summary>The same, nearest to 'near' (a drone going back where it came from).</summary>
        public static OrchestratorBlock StagingFor(IMyCubeGrid droneGrid, long droneOwner, Vector3D near)
        {
            var queue = AutomataSession.GetMessageQueue();
            OrchestratorBlock best = null;
            double bestD = double.MaxValue;
            for (int i = 0; i < All.Count; i++)
            {
                var o = All[i];
                if (!o.IsRunning || o.settings == null || !o.settings.StagingArea) continue;   // the parking protocol is on
                if (!Drone.AnchorDirectory.IsAllowed(o.OwnerId, droneOwner)) continue;
                double d = Vector3D.DistanceSquared(o.block.GetPosition(), near);
                bool linked = queue != null && queue.Relays.Reaches(o.block.CubeGrid, droneGrid, o.OwnerId, false, true);   // a docked drone is reached through its connectors
                if (!linked && d > 5000.0 * 5000.0) continue;
                if (d < bestD) { bestD = d; best = o; }
            }
            return best;
        }

        #region Terminal
        public bool Terminal_StagingArea
        {
            get { return settings.StagingArea; }
            set { if (settings.StagingArea == value) return; settings.StagingArea = value; SettingsEdited(); }
        }

        /// <summary>Parking protocol mode (combobox): ParkingProtocolMode as a number.</summary>
        public long Terminal_ParkingMode
        {
            get { return (long)settings.ParkingMode; }
            set
            {
                var m = value == (long)ParkingProtocolMode.ParkOnGrid ? ParkingProtocolMode.ParkOnGrid
                      : value == (long)ParkingProtocolMode.Voxel ? ParkingProtocolMode.Voxel : ParkingProtocolMode.ParkingArea;
                if (settings.ParkingMode == m) return;
                settings.ParkingMode = m;
                if (m != ParkingProtocolMode.ParkingArea) stagingDraw = false;
                SettingsEdited();
            }
        }

        /// <summary>Metres per block of the orchestrator's grid (slider texts).</summary>
        public double Terminal_GridSize { get { return GridSize; } }

        public float Terminal_GetStagingSize(int axis)
        {
            EnsureAreaInBlocks();
            return axis == 0 ? settings.StagingSizeX : axis == 1 ? settings.StagingSizeY : settings.StagingSizeZ;
        }

        public void Terminal_SetStagingSize(int axis, float v)
        {
            EnsureAreaInBlocks();
            v = SizeBlocks(v);
            if (Terminal_GetStagingSize(axis) == v) return;
            if (axis == 0) settings.StagingSizeX = v; else if (axis == 1) settings.StagingSizeY = v; else settings.StagingSizeZ = v;
            SettingsEdited();
        }

        public float Terminal_GetStagingOffset(int axis)
        {
            EnsureAreaInBlocks();
            return axis == 0 ? settings.StagingOffsetX : axis == 1 ? settings.StagingOffsetY : settings.StagingOffsetZ;
        }

        public void Terminal_SetStagingOffset(int axis, float v)
        {
            EnsureAreaInBlocks();
            v = MathHelper.Clamp((float)Math.Round(v), -MaxOffsetBlocks, MaxOffsetBlocks);
            if (Terminal_GetStagingOffset(axis) == v) return;
            if (axis == 0) settings.StagingOffsetX = v; else if (axis == 1) settings.StagingOffsetY = v; else settings.StagingOffsetZ = v;
            SettingsEdited();
        }

        /// <summary>Local drawing of the area (this player only, off after the server's DebugDrawTimeoutSeconds).</summary>
        public bool Terminal_StagingDraw
        {
            get { return stagingDraw; }
            set
            {
                stagingDraw = value && settings.StagingArea && settings.ParkingMode == ParkingProtocolMode.ParkingArea;
                stagingDrawStart = MyAPIGateway.Session.GameplayFrameCounter;
                if (AutomataSession.Instance != null) AutomataSession.Instance.RequestDraw(this, stagingDraw || laneDraw);
            }
        }
        #endregion

        #region Draw (clients)
        public bool DrawDebug()
        {
            bool area = DrawStaging();
            bool lane = DrawLane();
            return area || lane;
        }

        private bool DrawStaging()
        {
            if (!stagingDraw || Entity == null || Entity.MarkedForClose) { stagingDraw = false; return false; }
            if (MyAPIGateway.Session.GameplayFrameCounter - stagingDrawStart > DrawTimeoutTicks) { stagingDraw = false; return false; }   // the server's DebugDrawTimeoutSeconds
            MatrixD box;
            Vector3D half;
            if (!TryGetStagingArea(out box, out half)) { stagingDraw = false; return false; }
            var local = new BoundingBoxD(-half, half);
            Color faces = Color.DeepSkyBlue * 0.12f;
            Color grid = Color.DeepSkyBlue * 0.6f;
            var cells = new Vector3I(Math.Max(1, (int)Math.Round(half.X * 2 / 2.5)), Math.Max(1, (int)Math.Round(half.Y * 2 / 2.5)),
                                     Math.Max(1, (int)Math.Round(half.Z * 2 / 2.5)));
            MySimpleObjectDraw.DrawTransparentBox(ref box, ref local, ref faces, ref faces, MySimpleObjectRasterizer.Solid, cells,
                0.002f, StagingMaterial, null, false, -1, BlendTypeEnum.Standard);
            MySimpleObjectDraw.DrawTransparentBox(ref box, ref local, ref grid, ref grid, MySimpleObjectRasterizer.Wireframe, cells,
                0.002f, null, StagingMaterial, false, -1, BlendTypeEnum.SDR);
            return true;
        }

        public void StopDebugDraw()
        {
            stagingDraw = false;
            StopLaneDraw();
        }
        #endregion
    }
}
