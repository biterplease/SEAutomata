using System;
using System.Collections.Generic;

using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;

using Automata.Pointer;
using Automata.Util;
using Automata.Util.Logging;
using Automata.VirtualNetwork;

namespace Automata.Designator
{
    /// <summary>
    /// Target designator block (a programmable block subtype with the Custom Turret Controller's model, large and small
    /// grid): the camera designator. Its "Designator:" toolbar actions (TAG, SPOT, AREA, ENGAGE, ESCORT, RECALL, CLEAR) order
    /// drones as the hand-held designators do, pointed with a camera of this block's construct (ScoutPointer.CameraReceived,
    /// server). The camera: large or small grid, on the block's grid or mechanically connected to it (rotor, hinge, piston - not
    /// through connectors or magnetic plates), working, the owner's or their faction's. Reach ScoutPointer.CAMERA_RANGE;
    /// nobody needs to look through it.
    ///
    /// The block does no job work: an order is a DESIGNATOR_JOB_ANNOUNCEMENT over the virtual network (its grid's antennas /
    /// relay graph decide whether an orchestrator is in reach); the running orchestrator ingests it as a player job and answers on
    /// DESIGNATOR_ORDER_UPDATE (received / confirmed / cancelled). No answer within ORDER_ANSWER_SECONDS: no orchestrator in reach,
    /// the player gets the error sound and the LCD a line. It may share a grid with a Drone Controller (a "fleet leader" the
    /// player pilots): that drone never takes its orders, and ESCORT with no hit (or one within SELF_ESCORT_RADIUS) escorts
    /// the construct itself. Its own screen (and LCDs with its tag) show the tags counted and its orders (AutomataLog app).
    /// </summary>
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MyProgrammableBlock), false, "AutomataLargeTargetDesignator", "AutomataSmallTargetDesignator")]
    public class TargetDesignatorBlock : SettingsBlockLogic, INicknamed
    {
        #region UI strings
        private static readonly MyStringId StrNoCamera = MyStringId.GetOrCompute("(none)");
        private static readonly MyStringId StrNoCameraSelected = MyStringId.GetOrCompute("no designator camera selected");
        private static readonly MyStringId StrCameraGone = MyStringId.GetOrCompute("the designator camera is gone");
        private static readonly MyStringId StrCameraOff = MyStringId.GetOrCompute("the designator camera '{0}' isn't working");
        private static readonly MyStringId StrCameraDetached = MyStringId.GetOrCompute("the designator camera '{0}' isn't on this construct (rotor / hinge / piston)");
        private static readonly MyStringId StrCameraForeign = MyStringId.GetOrCompute("the designator camera '{0}' isn't yours or your faction's");
        private static readonly MyStringId StrRefused = MyStringId.GetOrCompute("Designator: {0}");
        private static readonly MyStringId StrNoOrchestrator = MyStringId.GetOrCompute("{0}: no orchestrator answered (in reach of an antenna?)");
        private static readonly MyStringId StrNoNetwork = MyStringId.GetOrCompute("{0}: the network isn't up");
        private static readonly MyStringId StrTitle = MyStringId.GetOrCompute("Designator {0}");
        private static readonly MyStringId StrTags = MyStringId.GetOrCompute("TAGS");
        private static readonly MyStringId StrIndustrial = MyStringId.GetOrCompute("Industrial: {0}");
        private static readonly MyStringId StrCombat = MyStringId.GetOrCompute("Combat: {0}");
        private static readonly MyStringId StrLastTagged = MyStringId.GetOrCompute("Last tagged:");
        private static readonly MyStringId StrCameraLine = MyStringId.GetOrCompute("Camera: {0}");
        private static readonly MyStringId StrInfoCamera = MyStringId.GetOrCompute("Designator camera: {0}");
        private static readonly MyStringId StrInfoOrders = MyStringId.GetOrCompute("Orders waiting for an answer: {0}");
        #endregion

        #region Timing
        private const double ORDER_ANSWER_SECONDS = 5;      // no answer from an orchestrator by then: none in reach
        private const double ORDER_FORGET_SECONDS = 90;     // an order's pending entry is dropped this long after it was sent
        #endregion

        /// <summary>Designators loaded on this machine.</summary>
        public static readonly List<TargetDesignatorBlock> All = new List<TargetDesignatorBlock>();

        private IMyTerminalBlock _block;
        private long entityId;
        private TargetDesignatorSettings settings = new TargetDesignatorSettings();
        private bool ready;
        private int infoCounter;
        private MessageQueue messaging;
        private GridAntennaReg antennaReg;
        private int messageCounter;
        private int idCounter;
        private readonly BlockLcd lcd = new BlockLcd();
        private readonly BlockLog log = new BlockLog();
        private readonly List<string> header = new List<string>();
        private readonly List<IMyCubeGrid> group = new List<IMyCubeGrid>();

        private sealed class PendingOrder
        {
            public long Identity;
            public string What;
            public DateTime Sent;
            public bool Answered;
        }
        private readonly Dictionary<uint, PendingOrder> pending = new Dictionary<uint, PendingOrder>();
        private readonly List<uint> pendingGone = new List<uint>();
        private readonly List<Message<DesignatorOrderUpdate>> updateInbox = new List<Message<DesignatorOrderUpdate>>();

        public long OwnerId { get { return _block != null ? _block.OwnerId : 0; } }
        public IMyTerminalBlock Block { get { return _block; } }

        #region Block
        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            base.Init(objectBuilder);
            _block = (IMyTerminalBlock)Entity;
            entityId = Entity.EntityId;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            base.UpdateOnceBeforeFrame();
            if (_block.CubeGrid == null || _block.CubeGrid.Physics == null) return;   // projections / ghosts
            TargetDesignatorTerminalControls.DoOnce();
            LoadFromStorage();
            _block.AppendingCustomInfo += AppendInfo;
            ready = true;
            if (!All.Contains(this)) All.Add(this);
            NeedsUpdate |= MyEntityUpdateEnum.EACH_10TH_FRAME;
            if (Log.DebugEnabled) Log.Debug("Designator {0}: '{1}' on '{2}'", entityId, _block.CustomName, _block.CubeGrid.DisplayName);
        }

        public override void UpdateBeforeSimulation10()
        {
            base.UpdateBeforeSimulation10();
            if (!ready || !IsServer) return;
            if (pending.Count > 0) UpdateOrders();
            if (++infoCounter >= 10)
            {
                infoCounter = 0;
                _block.RefreshCustomInfo();
                UpdateLcd();
            }
        }

        public override void Close()
        {
            try
            {
                if (_block != null) _block.AppendingCustomInfo -= AppendInfo;
                All.Remove(this);
                if (messaging != null) messaging.ReaderClosed(entityId);
            }
            catch (Exception ex)
            {
                Log.Error("Designator {0}: close error: {1}", entityId, ex);
            }
            base.Close();
        }

        protected override string SettingsXml()
        {
            return MyAPIGateway.Utilities.SerializeToXML(settings);
        }

        protected override bool LoadSettingsXml(string xml)
        {
            var loaded = MyAPIGateway.Utilities.SerializeFromXML<TargetDesignatorSettings>(xml);
            if (loaded == null) return false;
            settings = loaded;
            return true;
        }

        protected override string SaveText()
        {
            return BlockSave.ToText(new TargetDesignatorSaveData { Settings = settings, EntityId = Entity.EntityId });
        }

        protected override bool LoadSaveText(string text)
        {
            var data = BlockSave.FromText<TargetDesignatorSaveData>(text, Entity.EntityId);
            if (data == null) return false;
            settings = data.Settings ?? new TargetDesignatorSettings();
            return true;
        }

        protected override void OnSettingsChanged()
        {
        }
        #endregion

        #region Terminal
        public string Nickname { get { return settings.Nickname ?? ""; } }

        public string Terminal_Nickname
        {
            set { value = value ?? ""; if (settings.Nickname == value) return; settings.Nickname = value; SettingsEdited(); }
        }

        public string Terminal_LcdTag
        {
            get { return settings.LcdTag ?? ""; }
            set { if (settings.LcdTag == value) return; settings.LcdTag = value; SettingsEdited(); }
        }

        public LcdStyle Terminal_LcdStyle { get { if (settings.Lcd == null) settings.Lcd = new LcdStyle(); return settings.Lcd; } }
        public void Terminal_LcdEdited() { SettingsEdited(); }

        public long Terminal_Camera
        {
            get { return settings.CameraId; }
            set
            {
                if (settings.CameraId == value) return;
                settings.CameraId = value;
                SettingsEdited();
            }
        }

        /// <summary>Dropdown content: (none), then the cameras that may designate (see the class notes); the key is the camera's entity id.</summary>
        public void Terminal_CameraItems(List<MyTerminalControlComboBoxItem> items)
        {
            items.Add(new MyTerminalControlComboBoxItem { Key = 0L, Value = StrNoCamera });
            if (_block == null) return;
            group.Clear();
            MyAPIGateway.GridGroups.GetGroup(_block.CubeGrid, GridLinkTypeEnum.Mechanical, group);
            for (int g = 0; g < group.Count; g++)
                foreach (var cam in group[g].GetFatBlocks<IMyCameraBlock>())
                {
                    if (cam.Closed || !Drone.AnchorDirectory.IsAllowed(_block.OwnerId, cam.OwnerId)) continue;
                    items.Add(new MyTerminalControlComboBoxItem { Key = cam.EntityId, Value = MyStringId.GetOrCompute(cam.CustomName) });
                }
            group.Clear();
        }

        /// <summary>Dropdown selection: the chosen camera, or 0 (none) when the saved one is not on the list (gone, other owner, detached).</summary>
        public long Terminal_CameraShown
        {
            get
            {
                long current = settings.CameraId;
                if (current == 0 || _block == null) return 0;
                bool found = false;
                group.Clear();
                MyAPIGateway.GridGroups.GetGroup(_block.CubeGrid, GridLinkTypeEnum.Mechanical, group);
                for (int g = 0; g < group.Count && !found; g++)
                    foreach (var cam in group[g].GetFatBlocks<IMyCameraBlock>())
                        if (cam.EntityId == current && !cam.Closed && Drone.AnchorDirectory.IsAllowed(_block.OwnerId, cam.OwnerId)) { found = true; break; }
                group.Clear();
                return found ? current : 0;
            }
        }

        /// <summary>Client: a "Designator:" action (the server points it with the camera).</summary>
        public void Terminal_Designate(DesignatorOrder order)
        {
            var net = AutomataSession.Instance != null ? AutomataSession.Instance.Net : null;
            if (net != null) net.SendToServer(new DesignatorCameraPacket { DesignatorId = Entity.EntityId, Order = order });
        }

        private void AppendInfo(IMyTerminalBlock b, System.Text.StringBuilder target)
        {
            if (!ready) return;
            IMyEntity e;
            var cam = settings.CameraId != 0 && MyAPIGateway.Entities.TryGetEntityById(settings.CameraId, out e) ? e as IMyCameraBlock : null;
            target.AppendLoc(StrInfoCamera, cam != null && !cam.Closed ? cam.CustomName : Loc.T(StrNoCamera)).Append('\n');
            if (pending.Count > 0) target.AppendLoc(StrInfoOrders, pending.Count).Append('\n');
        }
        #endregion

        #region Camera
        /// <summary>The selected designator camera when it may designate now; null with why otherwise.</summary>
        public IMyCameraBlock DesignatorCamera(out string why)
        {
            why = null;
            long id = settings.CameraId;
            if (id == 0) { why = Loc.T(StrNoCameraSelected); return null; }
            IMyEntity e;
            var cam = MyAPIGateway.Entities.TryGetEntityById(id, out e) ? e as IMyCameraBlock : null;
            if (cam == null || cam.Closed) { why = Loc.T(StrCameraGone); return null; }
            if (!cam.IsWorking) { why = Loc.F(StrCameraOff, cam.CustomName); return null; }
            if (!Drone.AnchorDirectory.IsAllowed(_block.OwnerId, cam.OwnerId)) { why = Loc.F(StrCameraForeign, cam.CustomName); return null; }
            group.Clear();
            MyAPIGateway.GridGroups.GetGroup(_block.CubeGrid, GridLinkTypeEnum.Mechanical, group);
            bool attached = group.Contains(cam.CubeGrid);
            group.Clear();
            if (!attached) { why = Loc.F(StrCameraDetached, cam.CustomName); return null; }
            return cam;
        }

        /// <summary>A refused camera order, in the designator's log.</summary>
        public void ReportDesignator(string why)
        {
            log.Add(Loc.F(StrRefused, why));
        }

        /// <summary>
        /// The construct this designator is part of, for escorting it: the largest grid (by volume) of its mechanical group.
        /// Null when it has no physics yet.
        /// </summary>
        public IMyCubeGrid RootGrid()
        {
            if (_block == null || _block.CubeGrid == null) return null;
            group.Clear();
            MyAPIGateway.GridGroups.GetGroup(_block.CubeGrid, GridLinkTypeEnum.Mechanical, group);
            IMyCubeGrid best = _block.CubeGrid;
            double volume = -1;
            for (int g = 0; g < group.Count; g++)
            {
                var box = group[g].LocalAABB;   // metres
                double v = (double)box.Size.X * box.Size.Y * box.Size.Z;
                if (v > volume) { volume = v; best = group[g]; }
            }
            group.Clear();
            return best;
        }
        #endregion

        #region Orders (the network)
        /// <summary>
        /// Server: an order as a DESIGNATOR_JOB_ANNOUNCEMENT through the network (the grid's antennas / relay graph reach a running
        /// orchestrator or not). 'ja' carries the order fields (ScoutPointer builds it). False: the network isn't up.
        /// </summary>
        public bool SendOrder(JobAnnouncement ja)
        {
            if (!IsServer || ja == null) return false;
            if (messaging == null) messaging = AutomataSession.GetMessageQueue();
            if (messaging == null || !GridAntennaReg.Ensure(messaging, ref antennaReg, _block))
            {
                log.Add(Loc.F(StrNoNetwork, ja.What));
                return false;
            }
            ja.EntityType = AutomataEntityType.TargetDesignatorBlock;
            ja.OriginBlockId = entityId;
            var msg = new Message<JobAnnouncement>
            {
                Payload = ja,
                MessageId = IdGenerator.GenerateId(ref messageCounter, entityId),
                CreatedAt = TimeUtil.DateTimeToTimestamp(DateTime.UtcNow),
                SenderId = entityId,
                SenderOwnerId = _block.OwnerId,
                RecipientBlockType = MessageQueue.IAIBlockType.Orchestrator,
                Channel = Channel.DESIGNATOR_JOB_ANNOUNCEMENT,
            };
            var error = messaging.BroadcastMessage(antennaReg, msg, false, MessageQueue.IAIBlockType.TargetDesignator);
            if (error != ErrorCode.None)
            {
                if (Log.DebugEnabled) Log.Debug("Designator {0}: order {1} job {2} not sent: {3}", entityId, ja.OrderId, ja.JobId, error);
                log.Add(Loc.F(StrNoNetwork, ja.What));
                return false;
            }
            PendingOrder p;
            if (!pending.TryGetValue(ja.OrderId, out p)) pending[ja.OrderId] = new PendingOrder { Identity = ja.Identity, What = ja.What, Sent = DateTime.UtcNow };
            if (Log.DebugEnabled) Log.Debug("Designator {0}: order {1} job {2} ({3}) sent: {4}", entityId, ja.OrderId, ja.JobId, ja.Type, ja.What);
            return true;
        }

        /// <summary>A job id for an order given here (unique with the block's id).</summary>
        public uint NextId()
        {
            return IdGenerator.GenerateId(ref idCounter, entityId);
        }

        // Answers of the orchestrator; orders nobody answered
        private void UpdateOrders()
        {
            if (messaging == null) messaging = AutomataSession.GetMessageQueue();
            if (messaging != null && GridAntennaReg.Ensure(messaging, ref antennaReg, _block))
            {
                messaging.ReadMessages(entityId, MessageQueue.IAIBlockType.TargetDesignator, antennaReg, Channel.DESIGNATOR_ORDER_UPDATE, updateInbox, 20, true,
                                       PayloadType.DesignatorOrderUpdate);
                for (int i = 0; i < updateInbox.Count; i++)
                {
                    var u = updateInbox[i].Payload;
                    PendingOrder p;
                    if (u == null || !pending.TryGetValue(u.OrderId, out p)) continue;
                    p.Answered = true;
                    if (!string.IsNullOrEmpty(u.Text)) log.Add(u.Text);
                    if (u.State != DesignatorOrderState.Received) pending.Remove(u.OrderId);
                }
                updateInbox.Clear();
            }
            DateTime now = DateTime.UtcNow;
            pendingGone.Clear();
            foreach (var kv in pending)
            {
                double age = (now - kv.Value.Sent).TotalSeconds;
                if (!kv.Value.Answered && age >= ORDER_ANSWER_SECONDS)
                {
                    log.Add(Loc.F(StrNoOrchestrator, kv.Value.What));
                    ScoutPointer.Feedback(kv.Value.Identity, DesignatorSignal.Error);
                    pendingGone.Add(kv.Key);
                }
                else if (age >= ORDER_FORGET_SECONDS) pendingGone.Add(kv.Key);
            }
            for (int i = 0; i < pendingGone.Count; i++) pending.Remove(pendingGone[i]);
            pendingGone.Clear();
        }
        #endregion

        #region LCD
        // Every second, only while someone looks at its screen / an LCD with its tag
        private void UpdateLcd()
        {
            if (!lcd.Wanted(_block, settings.LcdTag)) return;
            header.Clear();
            if (Terminal_LcdStyle.ShowHeader)
            {
                int industrial, combat;
                long last;
                ScoutPointer.TagSummary(_block.OwnerId, out industrial, out combat, out last);
                header.Add(Loc.F(StrTitle, Nicknames.Of(Entity)));
                header.Add(Loc.T(StrTags));
                header.Add(BlockLcd.Columns(Loc.F(StrIndustrial, industrial), Loc.F(StrCombat, combat), 24));
                header.Add(Loc.T(StrLastTagged));
                header.Add(last != 0 ? Nicknames.Of(last) : "-");
                string why;
                var cam = DesignatorCamera(out why);
                header.Add(Loc.F(StrCameraLine, cam != null ? cam.CustomName : why));
            }
            lcd.Publish(Terminal_LcdStyle, header, log);
        }

        /// <summary>A line for the log (the orchestrator's answers, refusals).</summary>
        public void AddLog(string text)
        {
            log.Add(text);
        }
        #endregion
    }
}
