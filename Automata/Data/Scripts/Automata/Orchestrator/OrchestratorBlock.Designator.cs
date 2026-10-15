using System;
using System.Collections.Generic;

using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

using Automata.Pointer;
using Automata.Util;

namespace Automata.Orchestrator
{
    /// <summary>
    /// Camera designator: the "Designator:" toolbar actions (TAG, SPOT, AREA, ENGAGE, ESCORT, RECALL) order drones as the
    /// hand-held designators do, pointed with a camera of this orchestrator's construct (ScoutPointer.CameraReceived,
    /// server). The camera: large or small grid, on the orchestrator's grid or mechanically connected to it (rotor,
    /// hinge, piston - not through connectors or magnetic plates), working, the owner's or their faction's. Reach
    /// ScoutPointer.CAMERA_RANGE; nobody needs to look through it. "Designator: CLEAR" forgets the tags counted on the
    /// designator LCDs (no camera needed).
    /// </summary>
    public partial class OrchestratorBlock
    {
        private static readonly MyStringId StrNoCamera = MyStringId.GetOrCompute("(none)");
        private static readonly MyStringId StrDesignatorNoCamera = MyStringId.GetOrCompute("no designator camera selected");
        private static readonly MyStringId StrDesignatorCameraGone = MyStringId.GetOrCompute("the designator camera is gone");
        private static readonly MyStringId StrDesignatorCameraOff = MyStringId.GetOrCompute("the designator camera '{0}' isn't working");
        private static readonly MyStringId StrDesignatorCameraDetached = MyStringId.GetOrCompute("the designator camera '{0}' isn't on this construct (rotor / hinge / piston)");
        private static readonly MyStringId StrDesignatorCameraForeign = MyStringId.GetOrCompute("the designator camera '{0}' isn't yours or your faction's");
        private static readonly MyStringId StrDesignatorRefused = MyStringId.GetOrCompute("Designator: {0}");

        private readonly List<IMyCubeGrid> designatorGroup = new List<IMyCubeGrid>();

        public long Terminal_DesignatorCamera
        {
            get { return settings != null ? settings.DesignatorCameraId : 0; }
            set
            {
                if (settings == null || settings.DesignatorCameraId == value) return;
                settings.DesignatorCameraId = value;
                SettingsEdited();
            }
        }

        /// <summary>Listbox content: (none), then the cameras that may designate (see the class notes); the selected one marked.</summary>
        public void Terminal_CameraList(List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var none = new MyTerminalControlListBoxItem(StrNoCamera, MyStringId.NullOrEmpty, 0L);
            items.Add(none);
            long current = Terminal_DesignatorCamera;
            bool found = false;
            if (block != null)
            {
                designatorGroup.Clear();
                MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, designatorGroup);
                for (int g = 0; g < designatorGroup.Count; g++)
                    foreach (var cam in designatorGroup[g].GetFatBlocks<IMyCameraBlock>())
                    {
                        if (cam.Closed || !Drone.AnchorDirectory.IsAllowed(block.OwnerId, cam.OwnerId)) continue;
                        var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(cam.CustomName), MyStringId.NullOrEmpty, cam.EntityId);
                        items.Add(item);
                        if (cam.EntityId == current) { selected.Add(item); found = true; }
                    }
                designatorGroup.Clear();
            }
            if (!found) selected.Add(none);
        }

        public void Terminal_SelectCamera(List<MyTerminalControlListBoxItem> selected)
        {
            if (selected == null || selected.Count == 0 || !(selected[0].UserData is long)) return;
            Terminal_DesignatorCamera = (long)selected[0].UserData;
        }

        public string Terminal_DesignatorLcdTag
        {
            get { return settings != null ? settings.DesignatorLcdTag ?? "" : ""; }
            set { if (settings == null || settings.DesignatorLcdTag == value) return; settings.DesignatorLcdTag = value; SettingsEdited(); }
        }

        #region Designator LCDs (own tag, the orchestrator's LCD style)
        private static readonly MyStringId StrDesigTitle = MyStringId.GetOrCompute("Designator {0}");
        private static readonly MyStringId StrDesigTags = MyStringId.GetOrCompute("TAGS");
        private static readonly MyStringId StrDesigIndustrial = MyStringId.GetOrCompute("Industrial: {0}");
        private static readonly MyStringId StrDesigCombat = MyStringId.GetOrCompute("Combat: {0}");
        private static readonly MyStringId StrDesigLastTagged = MyStringId.GetOrCompute("Last tagged:");
        private static readonly MyStringId StrDesigAt = MyStringId.GetOrCompute("{0} at {1}");
        private static readonly MyStringId StrDesigEscorting = MyStringId.GetOrCompute("{0} escorting {1}");
        private static readonly MyStringId StrDesigEscortingMulti = MyStringId.GetOrCompute("Escorting {0}");

        private readonly BlockLcd designatorLcd = new BlockLcd(1);
        private readonly BlockLog designatorLog = new BlockLog();
        private readonly List<string> designatorHeader = new List<string>();

        // UpdateLcd: only while someone looks at a designator LCD
        private void UpdateDesignatorLcd()
        {
            string tag = settings.DesignatorLcdTag;
            if (string.IsNullOrEmpty(tag) || !designatorLcd.Wanted(block, tag)) return;
            designatorHeader.Clear();
            if (Terminal_LcdStyle.ShowHeader)
            {
                int industrial, combat;
                long last;
                ScoutPointer.TagSummary(block.OwnerId, out industrial, out combat, out last);
                designatorHeader.Add(Loc.F(StrDesigTitle, Nicknames.Of(Entity)));
                designatorHeader.Add(Loc.T(StrDesigTags));
                designatorHeader.Add(BlockLcd.Columns(Loc.F(StrDesigIndustrial, industrial), Loc.F(StrDesigCombat, combat), 24));
                designatorHeader.Add(Loc.T(StrDesigLastTagged));
                designatorHeader.Add(last != 0 ? Nicknames.Of(last) : "-");
            }
            designatorLcd.Publish(Terminal_LcdStyle, designatorHeader, designatorLog);
        }

        // A confirmed order (every job has its drone): one line for one drone, a heading and one indented line per drone for several
        private void LogDesignatorOrder(PlayerOrder order)
        {
            string label;
            switch (order.Type)
            {
                case JobType.DesignatorScoutPoint: label = "SPOT  "; break;
                case JobType.DesignatorScoutArea:  label = "AREA  "; break;
                case JobType.DesignatorEngage:     label = "ENGAGE"; break;
                case JobType.DesignatorEscort:     label = "ESCORT"; break;
                default: return;
            }
            var drones = new List<string>();
            foreach (var pj in playerJobs.Values)
                if (pj.OrderId == order.OrderId && pj.Assigned && pj.Drone != 0) drones.Add(Nicknames.Of(pj.Drone));
            if (drones.Count == 0) return;
            bool escort = order.Type == JobType.DesignatorEscort;
            string where = escort ? order.Name : GpsText(order.Position);
            var sb = new System.Text.StringBuilder(label).Append(": ");
            if (drones.Count == 1) sb.Append(escort ? Loc.F(StrDesigEscorting, drones[0], where) : Loc.F(StrDesigAt, drones[0], where));
            else
            {
                sb.Append(escort ? Loc.F(StrDesigEscortingMulti, where) : where);
                for (int i = 0; i < drones.Count; i++) sb.Append("\n  ").Append(drones[i]);
            }
            designatorLog.Add(sb.ToString());
        }

        // "GPS:x:y:z" (whole metres; short enough for an LCD line)
        private static string GpsText(VRageMath.Vector3D p)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return string.Format(inv, "GPS:{0:F0}:{1:F0}:{2:F0}", p.X, p.Y, p.Z);
        }
        #endregion

        /// <summary>Client: a "Designator:" action (the server points it with the camera).</summary>
        public void Terminal_Designate(DesignatorOrder order)
        {
            var net = AutomataSession.Instance != null ? AutomataSession.Instance.Net : null;
            if (net != null) net.SendToServer(new DesignatorCameraPacket { OrchestratorId = Entity.EntityId, Order = order });
        }

        /// <summary>The selected designator camera when it may designate now; null with why otherwise.</summary>
        public IMyCameraBlock DesignatorCamera(out string why)
        {
            why = null;
            long id = settings != null ? settings.DesignatorCameraId : 0;
            if (id == 0) { why = Loc.T(StrDesignatorNoCamera); return null; }
            IMyEntity e;
            var cam = MyAPIGateway.Entities.TryGetEntityById(id, out e) ? e as IMyCameraBlock : null;
            if (cam == null || cam.Closed) { why = Loc.T(StrDesignatorCameraGone); return null; }
            if (!cam.IsWorking) { why = Loc.F(StrDesignatorCameraOff, cam.CustomName); return null; }
            if (!Drone.AnchorDirectory.IsAllowed(block.OwnerId, cam.OwnerId)) { why = Loc.F(StrDesignatorCameraForeign, cam.CustomName); return null; }
            designatorGroup.Clear();
            MyAPIGateway.GridGroups.GetGroup(block.CubeGrid, GridLinkTypeEnum.Mechanical, designatorGroup);
            bool attached = designatorGroup.Contains(cam.CubeGrid);
            designatorGroup.Clear();
            if (!attached) { why = Loc.F(StrDesignatorCameraDetached, cam.CustomName); return null; }
            return cam;
        }

        /// <summary>A refused camera order, in the orchestrator's event log.</summary>
        public void ReportDesignator(string why)
        {
            AddEvent(Loc.F(StrDesignatorRefused, why));
        }
    }
}
