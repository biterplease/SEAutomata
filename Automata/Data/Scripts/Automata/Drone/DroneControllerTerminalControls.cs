using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using System;
using System.Collections.Generic;
using System.Text;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

using Automata.Config;
using Automata.Util;
using Automata.Util.Strings;
using Automata.Util.Logging;

namespace Automata.Drone
{
    public class DroneControllerTerminalControls
    {
        public static ServerConfig.DroneControllerBlockConfig BlockConfig = ServerConfig.Instance.Drone;

        const string IdPrefix = AutomataSession.MOD_NAME + "_";
        static bool Done = false;


        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;

            HideDefaultControls();
            HideDefaultActions();
            CreateControls();
            GateSections();
            CreateActions();
        }

        #region Toolbar actions
        const string ICON_TOGGLE = @"Textures\GUI\Icons\Actions\Toggle.dds";
        const string ICON_ON = @"Textures\GUI\Icons\Actions\SwitchOn.dds";
        const string ICON_OFF = @"Textures\GUI\Icons\Actions\SwitchOff.dds";
        const string ICON_START = @"Textures\GUI\Icons\Actions\Start.dds";

        /// <summary>Toolbar / button panel / PB actions. Same code paths as the terminal controls (synced there).</summary>
        static void CreateActions()
        {
            AddToggleActions("EnableAI", MyStrings.AI, l => l.Terminal_Enabled, (l, v) => l.Terminal_Enabled = v);
            AddToggleActions("DrawNavTargets", MyStrings.DrawNavigationTargets,
                l => l.Terminal_DrawNavigationTargets, (l, v) => l.Terminal_DrawNavigationTargets = v);
            AddToggleActions("DrawObservationArea", MyStrings.DrawObservationArea,
                l => l.Terminal_ObservationAreaDraw, (l, v) => l.Terminal_ObservationAreaDraw = v);
            AddToggleActions("UseBroadcastController", MyStrings.UseBroadcastController,
                l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v);
            AddAction("GoHome", Loc.T(MyStrings.GoHome), ICON_START, l => l.Terminal_GoHome(), null);
            AddAction("Stop", Loc.T(MyStrings.Stop), ICON_START, l => l.Terminal_StopOrders(), null);
        }

        // 'id' (toggle), 'id'_On, 'id'_Off; the toolbar slot shows On / Off
        static void AddToggleActions(string id, MyStringId name, Func<DroneControllerBlock, bool> get, Action<DroneControllerBlock, bool> set)
        {
            Action<DroneControllerBlock, StringBuilder> writer = (l, sb) => sb.AppendLoc(get(l) ? MyStrings.On : MyStrings.Off);
            AddAction(id, Loc.F(MyStrings.ActionToggle, Loc.T(name)), ICON_TOGGLE, l => { set(l, !get(l)); l.Terminal_Refresh(); }, writer);
            AddAction(id + "_On", Loc.F(MyStrings.ActionOn, Loc.T(name)), ICON_ON, l => { if (!get(l)) { set(l, true); l.Terminal_Refresh(); } }, writer);
            AddAction(id + "_Off", Loc.F(MyStrings.ActionOff, Loc.T(name)), ICON_OFF, l => { if (get(l)) { set(l, false); l.Terminal_Refresh(); } }, writer);
        }

        static void AddAction(string id, string name, string icon, Action<DroneControllerBlock> act, Action<DroneControllerBlock, StringBuilder> writer)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyRemoteControl>(IdPrefix + id);
            a.Name = new StringBuilder(name);
            a.ValidForGroups = true;
            a.Icon = icon;
            a.Action = (b) => { var l = GetBlock(b); if (l != null) act(l); };
            a.Writer = (b, sb) => { var l = GetBlock(b); if (l != null && writer != null) writer(l, sb); };
            a.Enabled = CustomVisibleCondition;   // drone controllers only (called per tick while in a toolbar: keep cheap)
            MyAPIGateway.TerminalControls.AddAction<IMyRemoteControl>(a);
        }
        #endregion

        /// <summary>
        /// Check an return the GameLogic object
        /// </summary>
        /// <param name="block"></param>
        /// <returns></returns>
        private static DroneControllerBlock GetBlock(IMyTerminalBlock block)
        {
            if (block != null && block.GameLogic != null) return block.GameLogic.GetAs<DroneControllerBlock>();
            return null;
        }

        static bool AppendedCondition(IMyTerminalBlock block)
        {
            // if block has this gamelogic component then return false to hide the control/action.
            return block?.GameLogic?.GetAs<DroneControllerBlock>() == null;
        }
        static bool CustomVisibleCondition(IMyTerminalBlock b)
        {
            // only visible for the blocks having this gamelogic comp
            return b?.GameLogic?.GetAs<DroneControllerBlock>() != null;
        }
        private static void AddSlider(string id, MyStringId title, MyStringId tooltip, float min, float max,
            Func<DroneControllerBlock, float> get, Action<DroneControllerBlock, float> set, Action<DroneControllerBlock, StringBuilder> write)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            c.SetLimits(min, max);
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : min; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) write(l, sb); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddCheckbox(string id, MyStringId title, MyStringId tooltip,
            Func<DroneControllerBlock, bool> get, Action<DroneControllerBlock, bool> set, bool multipleBlocks = true)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddColor(string id, MyStringId title, MyStringId tooltip,
            Func<DroneControllerBlock, Color> get, Action<DroneControllerBlock, Color> set)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : Color.White; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddLabel(string id, MyStringId text)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyRemoteControl>(IdPrefix + id);
            c.Label = text;
            c.SupportsMultipleBlocks = true;
            c.Visible = CustomVisibleCondition;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddSeparator(string id)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyRemoteControl>(IdPrefix + id);
            c.SupportsMultipleBlocks = true;
            c.Visible = CustomVisibleCondition;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AppendSpeed(StringBuilder sb, float v)
        {
            sb.AppendLoc(MyStrings.MetresPerSecond, v.ToString(v < 10 ? "F1" : "F0"));
        }

        private static void AppendPercent(StringBuilder sb, float v)
        {
            sb.Append(v.ToString("F0")).Append('%');
        }

        private static void AddWorkModeCheckbox(string id, MyStringId title, MyStringId tooltip, Construction.WorkModes flag)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Enabled = JobsCondition;
            c.Getter = (b) => { var l = GetBlock(b); return l != null && l.Terminal_GetWorkMode(flag); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_SetWorkMode(flag, v); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        // Observation area: stand-alone drones only (others take jobs where they are sent)
        static bool ObservationCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.Terminal_OperationModeValue == (long)OperationMode.StandAlone;
        }

        // GPS observation area: the offsets don't apply (the area is centred on the point)
        static bool ObservationOffsetCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return ObservationCondition(b) && logic != null && !logic.Terminal_ObservationAtGps;
        }

        static bool ObservationGpsCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return ObservationCondition(b) && logic != null && logic.Terminal_ObservationAtGps;
        }

        // The player's GPS points (client side); the saved one first when it isn't among them (set by someone else)
        private sealed class GpsChoice { public string Name; public Vector3D Position; }
        private static readonly List<IMyGps> gpsBuffer = new List<IMyGps>();

        private static void ObservationGpsContent(DroneControllerBlock l, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            string savedName = l.Terminal_ObservationGpsName;
            Vector3D? savedPos = l.Terminal_ObservationGpsPosition;
            bool savedListed = false;
            gpsBuffer.Clear();
            var player = MyAPIGateway.Session != null ? MyAPIGateway.Session.Player : null;
            if (player != null && MyAPIGateway.Session.GPS != null) gpsBuffer.AddRange(MyAPIGateway.Session.GPS.GetGpsList(player.IdentityId));
            for (int i = 0; i < gpsBuffer.Count; i++)
            {
                var g = gpsBuffer[i];
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(g.Name), MyStringId.GetOrCompute(FormatGps(g.Coords)),
                                                            new GpsChoice { Name = g.Name, Position = g.Coords });
                items.Add(item);
                if (savedPos.HasValue && g.Name == savedName && Vector3D.DistanceSquared(g.Coords, savedPos.Value) < 0.01)
                {
                    selected.Add(item);
                    savedListed = true;
                }
            }
            gpsBuffer.Clear();
            if (savedPos.HasValue && !savedListed)
            {
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute((savedName ?? "GPS") + " *"), MyStringId.GetOrCompute(FormatGps(savedPos.Value)),
                                                            new GpsChoice { Name = savedName, Position = savedPos.Value });
                items.Insert(0, item);
                selected.Add(item);
            }
        }

        private static string FormatGps(Vector3D p)
        {
            return string.Format("X {0:F0}  Y {1:F0}  Z {2:F0}", p.X, p.Y, p.Z);
        }

        private static void ObservationGpsSelected(DroneControllerBlock l, List<MyTerminalControlListBoxItem> selected)
        {
            if (selected.Count == 0) return;
            var choice = selected[0].UserData as GpsChoice;
            if (choice != null) l.Terminal_SetObservationGps(choice.Name, choice.Position);
        }

        // Mining / farming: the game's ores (crops) on the left, the ignored ones on the right, add / remove between them
        private static void AddIgnoreLists(bool crops)
        {
            string id = crops ? "Crops" : "Ores";
            AddListbox("Listbox_Known" + id, crops ? MyStrings.Crops : MyStrings.Crops,
                crops ? MyStrings.KnownCropsTip : MyStrings.KnownOresTip,
                6, true, (l, items, sel) => l.Terminal_KnownListContent(crops, items, sel), (l, sel) => l.Terminal_SelectKnown(crops, sel), null, true);
            AddButton("Button_Ignore" + id, MyStrings.IgnoreSelected,
                MyStrings.BLANK, l => l.Terminal_AddToIgnore(crops), null, true);
            AddListbox("Listbox_Ignored" + id, crops ? MyStrings.IgnoredCrops : MyStrings.IgnoredOres,
                MyStrings.BLANK,
                4, true, (l, items, sel) => l.Terminal_IgnoredListContent(crops, items, sel), (l, sel) => l.Terminal_SelectIgnored(crops, sel), null, true);
            AddButton("Button_Unignore" + id, MyStrings.RemoveSelected,
                MyStrings.BLANK, l => l.Terminal_RemoveFromIgnore(crops), null, true);
        }

        static bool HomeConnectorVisibleCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.HasHomeConnector;
        }

        // in blocks of 2.5 m: size 1–10, offset ±5 (the block clamps and rounds again)
        private static void AddObservationSlider(string id, MyStringId title, MyStringId tooltip, int axis, bool size)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            c.Enabled = size ? (Func<IMyTerminalBlock, bool>)ObservationCondition : ObservationOffsetCondition;
            if (size) c.SetLimits(DroneControllerBlock.OBSERVATION_SIZE_MIN, DroneControllerBlock.OBSERVATION_SIZE_MAX);
            else c.SetLimits(-DroneControllerBlock.OBSERVATION_OFFSET_MAX, DroneControllerBlock.OBSERVATION_OFFSET_MAX);
            c.Getter = (b) =>
            {
                var logic = GetBlock(b);
                if (logic == null) return size ? 5f : 0f;
                return size ? logic.Terminal_GetObservationSize(axis) : logic.Terminal_GetObservationOffset(axis);
            };
            c.Setter = (b, v) =>
            {
                var logic = GetBlock(b);
                if (logic == null) return;
                if (size) logic.Terminal_SetObservationSize(axis, v);
                else logic.Terminal_SetObservationOffset(axis, v);
            };
            c.Writer = (b, sb) =>
            {
                var logic = GetBlock(b);
                if (logic == null) return;
                int v = size ? logic.Terminal_GetObservationSize(axis) : logic.Terminal_GetObservationOffset(axis);
                sb.AppendLoc(v == 1 || v == -1 ? MyStrings.BlockMetres : MyStrings.BlocksMetres, v, (v * DroneControllerBlock.OBSERVATION_UNIT).ToString("F1"));
            };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        /// <summary>
        /// Hides default controls in the inherited Remote Control.
        /// </summary>
        // Remote control controls the drone keeps (On / Off and the generic terminal fields); every other one is hidden
        private static readonly HashSet<string> defaultControlIdsToKeep = new HashSet<string>
        {
            "ShowInTerminal",
            "ShowInToolbarConfig",
            "Name",
            "ShowOnHUD",
            "CustomData",
            "Control",
        };
        private static readonly HashSet<string> defaultActionIdsToKeep = new HashSet<string>
        {
            "ShowOnHUD",
            "ShowOnHUD_On",
            "ShowOnHUD_Off",
            "Control",
        };
        private static readonly HashSet<string> defaultExplicitRemoveControls = new HashSet<string>
        {
            "ToggleNavMarkers",
            "GpsList",
            "WaypointList",
        };
        private static readonly HashSet<string> defaultExplicitRemoveActions = new HashSet<string>
        {
            "ToggleNavMarkers",
        };

        /// <summary>
        /// Hides the remote control's own controls on drone controllers (vanilla remote controls keep them): all but
        /// On / Off and the generic terminal fields. Runs before the drone's controls are added.
        /// </summary>
        public static void HideDefaultControls()
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);

            foreach (IMyTerminalControl c in controls)
            {
                if (Log.DebugEnabled) Log.Debug("init print actions: action {0}", c.Id);
                if (defaultExplicitRemoveControls.Contains(c.Id) || c.Id != null && c.Id.StartsWith(IdPrefix))
                {
                    c.Visible = TerminalChainedDelegate.Create(c.Visible, AppendedCondition);    
                    continue;
                }
                else if (defaultControlIdsToKeep.Contains(c.Id) || (c.Id != null && c.Id.StartsWith(IdPrefix)))
                {
                    continue;
                }
                c.Visible = TerminalChainedDelegate.Create(c.Visible, AppendedCondition);
            }
        }

        /// <summary>
        /// Hides default actions in the Programmable Block.
        /// </summary>
        public static void HideDefaultActions()
        {
            List<IMyTerminalAction> actions;
            MyAPIGateway.TerminalControls.GetActions<IMyRemoteControl>(out actions);

            foreach (IMyTerminalAction a in actions)
            {
                if (defaultExplicitRemoveActions.Contains(a.Id) || a.Id != null && a.Id.StartsWith(IdPrefix))
                {
                    a.Enabled = TerminalChainedDelegate.Create(a.Enabled, AppendedCondition);    
                    continue;
                }
                if (defaultActionIdsToKeep.Contains(a.Id) || (a.Id != null && a.Id.StartsWith(IdPrefix)))
                    continue;
                a.Enabled = TerminalChainedDelegate.Create(a.Enabled, AppendedCondition);
            }
        }
        #region Control helpers (conditions, compact builders)
        static bool AiOffCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && !logic.Terminal_Enabled;
        }

        // Job controls: any mode but "managed by player"
        static bool JobsCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && !logic.IsManagedByPlayer;
        }

        // Debug controls: "managed by player" only
        static bool DebugCondition(IMyTerminalBlock b)
        {
            var logic = GetBlock(b);
            return logic != null && logic.IsManagedByPlayer;
        }

        static bool NeverCondition(IMyTerminalBlock b)
        {
            return false;
        }

        private static void AddButton(string id, MyStringId title, MyStringId tooltip, Action<DroneControllerBlock> action,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Action = (b) => { var l = GetBlock(b); if (l != null) action(l); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddTextbox(string id, MyStringId title, MyStringId tooltip,
            Func<DroneControllerBlock, StringBuilder> get, Action<DroneControllerBlock, StringBuilder> set,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Getter = (b) => { var l = GetBlock(b); return l != null ? get(l) : new StringBuilder(); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void AddListbox(string id, MyStringId title, MyStringId tooltip, int rows, bool multiselect,
            Action<DroneControllerBlock, List<MyTerminalControlListBoxItem>, List<MyTerminalControlListBoxItem>> content,
            Action<DroneControllerBlock, List<MyTerminalControlListBoxItem>> select,
            Func<IMyTerminalBlock, bool> enabled = null, bool multipleBlocks = false)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyRemoteControl>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.Visible = CustomVisibleCondition;
            if (enabled != null) c.Enabled = enabled;
            c.Multiselect = multiselect;
            c.VisibleRowsCount = rows;
            c.ListContent = (b, items, selected) => { var l = GetBlock(b); if (l != null) content(l, items, selected); };
            c.ItemSelected = (b, selected) => { var l = GetBlock(b); if (l != null) select(l, selected); };
            c.SupportsMultipleBlocks = multipleBlocks;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        private static void SetEnabled(string id, Func<IMyTerminalBlock, bool> enabled)
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);
            foreach (var c in controls)
                if (c.Id == IdPrefix + id) { c.Enabled = enabled; return; }
        }

        private static void SetVisible(string id, Func<IMyTerminalBlock, bool> visible)
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);
            foreach (var c in controls)
                if (c.Id == IdPrefix + id) { c.Visible = visible; return; }
        }

        // Sections fold: a header checkbox shows / hides the controls after it (until the next header). Local to this
        // player's terminal (all drone controllers alike), not saved or synced. GateSections chains the visibilities.
        private const string SECTION_PREFIX = "Checkbox_Section_";
        private static readonly Dictionary<string, bool> sectionShown = new Dictionary<string, bool>();

        private static bool SectionShown(string id)
        {
            bool shown;
            return sectionShown.TryGetValue(id, out shown) && shown;
        }

        private static void AddSection(string id, MyStringId title)
        {
            AddSeparator("Separator_" + id);
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + SECTION_PREFIX + id);
            c.Title = title;
            c.Tooltip = MyStrings.SectionTip;
            c.OnText = MySpaceTexts.SwitchText_On;
            c.OffText = MySpaceTexts.SwitchText_Off;
            c.Visible = CustomVisibleCondition;
            c.Getter = (b) => SectionShown(id);
            c.Setter = (b, v) => { sectionShown[id] = v; var l = GetBlock(b); if (l != null) l.Terminal_Refresh(); };
            c.SupportsMultipleBlocks = true;
            MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
        }

        // Every control after a section header is visible only while that header is ticked (and its own condition holds)
        private static void GateSections()
        {
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyRemoteControl>(out controls);
            string section = null;
            foreach (var ctl in controls)
            {
                if (ctl.Id == null || !ctl.Id.StartsWith(IdPrefix)) continue;
                string local = ctl.Id.Substring(IdPrefix.Length);
                if (local.StartsWith(SECTION_PREFIX)) { section = local.Substring(SECTION_PREFIX.Length); continue; }
                if (section == null || local.StartsWith("Separator_")) continue;
                string s = section;
                var visible = ctl.Visible;
                ctl.Visible = b => SectionShown(s) && (visible == null || visible(b));
            }
        }
        #endregion

        /// <summary>
        /// Layout, top to bottom (the block's own On/Off comes first): Enable AI, operation mode, behaviour profile; then
        /// folding sections (a header checkbox each, any profile): home connector, flight, power, LCD output, observation
        /// area (stand-alone only), welder, grinder, mining, farming, combat handover, jobs, debug.
        /// </summary>
        public static void CreateControls()
        {
            NicknameControl.Add<IMyRemoteControl, DroneControllerBlock>(IdPrefix + "Drone", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            // --- AI ---
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_Enabled");
                c.Title = MyStrings.EnableAI;
                c.Tooltip = MyStrings.BLANK;
                c.OnText = MyStrings.On;
                c.OffText = MyStrings.Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_Enabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_Enabled = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_OperationMode");
                c.Title = MyStrings.OperationMode;
                c.Tooltip = MyStrings.OperationModeTip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = AiOffCondition;
                c.ComboBoxContent = (list) =>
                {
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.StandAlone, Value = MyStrings.StandAlone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.ManagedByOrchestrator, Value = MyStrings.ManagedByOrchestrator });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)OperationMode.ManagedByPlayer, Value = MyStrings.ManagedByPlayer });
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_OperationModeValue ?? 0;
                c.Setter = (b, v) =>
                {
                    var l = GetBlock(b);
                    if (l != null && !l.Terminal_Enabled) l.Terminal_OperationModeValue = v;
                };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }


            // --- Behavior profile and ore survey: right under the mode (they decide which jobs the drone takes) ---
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_BehaviourProfile");
                c.Title = MyStrings.BehaviourProfile;
                c.Tooltip = MyStrings.BehaviourProfileTip;
                c.Visible = CustomVisibleCondition;
                c.ComboBoxContent = (list) =>
                {
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.None, Value = MyStrings.None });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsCargoDrone, Value = MyStrings.CargoDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsOreScout, Value = MyStrings.OreScout });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsCombatDrone, Value = MyStrings.CombatDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsOreHauler, Value = MyStrings.OreHauler });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderDrone, Value = MyStrings.WelderDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderRepair, Value = MyStrings.WelderRepair });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderProjected, Value = MyStrings.WelderProjected });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderUnbuilt, Value = MyStrings.WelderUnbuilt });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsGrinderDrone, Value = MyStrings.GrinderDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsWelderGrinderDrone, Value = MyStrings.WelderGrinderDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsMiningDrone, Value = MyStrings.MiningDrone });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsFarmingDrone, Value = MyStrings.FarmingDroneNotImplemented });
                    list.Add(new MyTerminalControlComboBoxItem { Key = (long)BehaviourProfile.IsRover, Value = MyStrings.Rover });
                };
                c.Getter = (b) => GetBlock(b)?.Terminal_BehaviourProfileValue ?? 0;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_BehaviourProfileValue = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddCheckbox("Checkbox_UseBroadcastController", MyStrings.UseBroadcastController,
                MyStrings.BLANK,
                l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v);
            AddButton("Button_GoHome", MyStrings.GoHome,
                MyStrings.GoHomeTip,
                l => l.Terminal_GoHome(), HomeConnectorVisibleCondition, true);
            AddButton("Button_StopOrders", MyStrings.StopOrders,
                MyStrings.StopOrdersTip,
                l => l.Terminal_StopOrders(), null, true);
            // --- Copy / paste settings (this player's clipboard) ---
            AddButton("Button_CopySettings", MyStrings.CopySettings,
                MyStrings.DroneCopySettingsTip,
                l => l.Terminal_CopySettings());
            AddButton("Button_PasteSettings", MyStrings.PasteSettings,
                MyStrings.DronePasteSettingsTip,
                l => l.Terminal_PasteSettings(), b => DroneControllerBlock.Terminal_HasCopiedSettings, true);
            // --- Home connector ---
            AddSection("Home", MyStrings.HomeConnector);
            AddListbox("Listbox_HomeConnector", MyStrings.HomeConnector,
                MyStrings.HomeConnectorTip,
                5, false, (l, items, sel) => l.Terminal_HomeConnectorListContent(items, sel), (l, sel) => l.Terminal_SelectHomeConnector(sel));
            AddButton("Button_ScanConnectors", MyStrings.Scan,
                MyStrings.ScanConnectorsTip, l => l.Terminal_ScanAnchors());
            AddTextbox("Textbox_ConnectorNameQuery", MyStrings.ConnectorNameQuery,
                MyStrings.BLANK,
                l => l.Textbox_ConnectorNameQuery, (l, v) => l.Textbox_ConnectorNameQuery = v);
            AddButton("Button_AddConnectorByName", MyStrings.AddToList,
                MyStrings.BLANK,
                l => l.Terminal_AddConnectorByName());
            AddButton("Button_SetCurrentAsHome", MyStrings.SetCurrentAsHome,
                MyStrings.SetCurrentAsHomeTip,
                l => l.Terminal_SetCurrentAsHome());
            AddCheckbox("Checkbox_HomeNotStation", MyStrings.HomeIsNotStatic,
                MyStrings.HomeIsNotStaticTip,
                l => l.Terminal_HomeNotStatic, (l, v) => { l.Terminal_HomeNotStatic = v; }, false);   // home: one drone at a time
            AddListbox("Listbox_HomeBeacon", MyStrings.HomeBeacon,
                MyStrings.HomeBeaconTip,
                4, false, (l, items, sel) => l.Terminal_HomeBeaconListContent(items, sel), (l, sel) => l.Terminal_SelectHomeBeacon(sel),
                b => { var l = GetBlock(b); return l != null && l.Terminal_HomeNotStatic; });
            AddButton("Button_GoHome2", MyStrings.GoHome,
                MyStrings.GoHomeTip,
                l => l.Terminal_GoHome(), HomeConnectorVisibleCondition, true);
            // --- Flight ---
            AddSection("Flight", MyStrings.Flight);
            AddCheckbox("Checkbox_EconomyFlight", MyStrings.EconomyFlight,
                MyStrings.EconomyFlightTip,
                l => l.Terminal_EconomyFlight, (l, v) => l.Terminal_EconomyFlight = v, multipleBlocks: true);
            AddSlider("Slider_MaxThrust", MyStrings.MaxThrust,
                MyStrings.MaxThrustTip,
                DroneControllerBlock.MAX_THRUST_MIN, DroneControllerBlock.MAX_THRUST_MAX,
                l => l.Terminal_MaxThrustPercent, (l, v) => l.Terminal_MaxThrustPercent = v, (l, sb) => AppendPercent(sb, l.Terminal_MaxThrustPercent));
            AddSlider("Slider_MaxSpeed", MyStrings.MaxSpeed,
                MyStrings.MaxSpeed,
                DroneControllerBlock.MAX_SPEED_MIN, DroneControllerBlock.MAX_SPEED_MAX,
                l => l.Terminal_MaxSpeed, (l, v) => l.Terminal_MaxSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_MaxSpeed));
            AddSlider("Slider_ApproachSpeed", MyStrings.ApproachSpeed,
                MyStrings.ApproachSpeedTip,
                DroneControllerBlock.APPROACH_SPEED_MIN, DroneControllerBlock.APPROACH_SPEED_MAX,
                l => l.Terminal_ApproachSpeed, (l, v) => l.Terminal_ApproachSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_ApproachSpeed));
            AddSlider("Slider_SafeSpeed", MyStrings.SafeSpeed,
                MyStrings.SafeSpeedTip,
                DroneControllerBlock.SAFE_SPEED_MIN, DroneControllerBlock.SAFE_SPEED_MAX,
                l => l.Terminal_SafeSpeed, (l, v) => l.Terminal_SafeSpeed = v, (l, sb) => AppendSpeed(sb, l.Terminal_SafeSpeed));
            AddSlider("Slider_WaypointTolerance", MyStrings.WaypointTolerance,
                MyStrings.WaypointToleranceTip,
                DroneControllerBlock.WAYPOINT_TOLERANCE_MIN, DroneControllerBlock.WAYPOINT_TOLERANCE_MAX,
                l => l.Terminal_WaypointTolerance, (l, v) => l.Terminal_WaypointTolerance = v,
                (l, sb) => sb.AppendLoc(MyStrings.Metres, l.Terminal_WaypointTolerance.ToString("F1")));
            AddSlider("Slider_MinAltitude", MyStrings.MinAltitude,
                MyStrings.MinAltitudeTip,
                0f, DroneControllerBlock.MIN_ALTITUDE_MAX,
                l => l.Terminal_MinAltitude, (l, v) => l.Terminal_MinAltitude = v,
                (l, sb) => { if (l.Terminal_MinAltitude <= 0) sb.AppendLoc(MyStrings.Off); else sb.AppendLoc(MyStrings.Metres, l.Terminal_MinAltitude.ToString("F0")); });
            AddCheckbox("Checkbox_AlignToPGravity", MyStrings.AlignToPGravity,
                MyStrings.AlignToPGravityTip,
                l => l.Terminal_AlignToPGravity, (l, v) => { l.Terminal_AlignToPGravity = v; l.Terminal_Refresh(); });
            AddSlider("Slider_MaxPitchDegrees", MyStrings.MaxPitchDegrees,
                MyStrings.BLANK, 0f, 90f,
                l => l.Terminal_MaxPitchDegrees, (l, v) => l.Terminal_MaxPitchDegrees = v,
                (l, sb) => sb.AppendLoc(MyStrings.Degrees, l.Terminal_MaxPitchDegrees.ToString("F1")));
            AddSlider("Slider_MaxRollDegrees", MyStrings.MaxRollDegrees,
                MyStrings.BLANK, 0f, 90f,
                l => l.Terminal_MaxRollDegrees, (l, v) => l.Terminal_MaxRollDegrees = v,
                (l, sb) => sb.AppendLoc(MyStrings.Degrees, l.Terminal_MaxRollDegrees.ToString("F1")));
            SetEnabled("Slider_MaxPitchDegrees", b => { var l = GetBlock(b); return l != null && l.Terminal_AlignToPGravity; });
            SetEnabled("Slider_MaxRollDegrees", b => { var l = GetBlock(b); return l != null && l.Terminal_AlignToPGravity; });

            AddSlider("Slider_MaxLoadGravity", MyStrings.MaxLoadGravity,
                MyStrings.MaxLoadGravityTip,
                0f, 200f,
                l => l.Terminal_MaxLoadGravity, (l, v) => l.Terminal_MaxLoadGravity = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, true));
            AddSlider("Slider_MaxLoadSpace", MyStrings.MaxLoadSpace,
                MyStrings.MaxLoadSpaceTip,
                0f, 200f,
                l => l.Terminal_MaxLoadSpace, (l, v) => l.Terminal_MaxLoadSpace = v, (l, sb) => l.Terminal_WriteMaxLoad(sb, false));

            // --- Power monitoring ---
            AddSection("Power", MyStrings.Power);
            AddCheckbox("Checkbox_AlwaysRefuel", MyStrings.AlwaysRefuel,
                MyStrings.AlwaysRefuelTip,
                l => l.Terminal_AlwaysRefuel, (l, v) => l.Terminal_AlwaysRefuel = v);
            AddCheckbox("Checkbox_MonitorBattery", MyStrings.MonitorBattery,
                MyStrings.MonitorBatteryTip,
                l => l.Terminal_MonitorBattery, (l, v) => { l.Terminal_MonitorBattery = v; l.Terminal_Refresh(); });
            AddSlider("Slider_BatteryRefuelThreshold", MyStrings.BatteryRefuelThreshold,
                MyStrings.BatteryRefuelThresholdTip, 5f, 50f,
                l => l.Terminal_BatteryRefuelThreshold, (l, v) => l.Terminal_BatteryRefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryRefuelThreshold));
            AddSlider("Slider_BatteryOperationalThreshold", MyStrings.BatteryOperationalThreshold,
                MyStrings.ReturnToWork, 10f, 95f,
                l => l.Terminal_BatteryOperationalThreshold, (l, v) => l.Terminal_BatteryOperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_BatteryOperationalThreshold));
            AddCheckbox("Checkbox_MonitorHydrogen", MyStrings.MonitorHydrogen,
                MyStrings.MonitorHydrogenTip,
                l => l.Terminal_MonitorHydrogen, (l, v) => { l.Terminal_MonitorHydrogen = v; l.Terminal_Refresh(); });
            AddSlider("Slider_H2RefuelThreshold", MyStrings.H2RefuelThreshold,
                MyStrings.H2RefuelThresholdTip, 5f, 50f,
                l => l.Terminal_H2RefuelThreshold, (l, v) => l.Terminal_H2RefuelThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2RefuelThreshold));
            AddSlider("Slider_H2OperationalThreshold", MyStrings.H2OperationalThreshold,
                MyStrings.ReturnToWork, 10f, 95f,
                l => l.Terminal_H2OperationalThreshold, (l, v) => l.Terminal_H2OperationalThreshold = v, (l, sb) => AppendPercent(sb, l.Terminal_H2OperationalThreshold));
            SetEnabled("Slider_BatteryRefuelThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorBattery; });
            SetEnabled("Slider_BatteryOperationalThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorBattery; });
            SetEnabled("Slider_H2RefuelThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorHydrogen; });
            SetEnabled("Slider_H2OperationalThreshold", b => { var l = GetBlock(b); return l != null && l.Terminal_MonitorHydrogen; });

            // --- LCD / terminal log (player-facing; the server log is set in the mod config) ---
            AddSection("Lcd", MyStrings.Lcd);
            AddCheckbox("Checkbox_LcdShowHeader", MyStrings.LcdShowHeader,
                MyStrings.BLANK,
                l => l.Terminal_LcdShowHeader, (l, v) => l.Terminal_LcdShowHeader = v);
            AddTextbox("Textbox_LCDScreenTag", MyStrings.LCDScreenTag,
                MyStrings.LCDScreenTagTip,
                l => l.Terminal_LCDScreenTag, (l, v) => l.Terminal_LCDScreenTag = v, null, true);
            AddColor("Color_LcdForeground", MyStrings.LcdForeground, MyStrings.BLANK,
                l => l.Terminal_LcdForeground, (l, v) => l.Terminal_LcdForeground = v);
            AddColor("Color_LcdBackground", MyStrings.LcdBackground, MyStrings.BLANK,
                l => l.Terminal_LcdBackground, (l, v) => l.Terminal_LcdBackground = v);
            AddSlider("Slider_LcdFontSize", MyStrings.LcdFontSize,
                MyStrings.BLANK, 0.1f, 2f,
                l => l.Terminal_LcdFontSize, (l, v) => l.Terminal_LcdFontSize = v, (l, sb) => sb.Append(l.Terminal_LcdFontSize.ToString("F2")));


            // --- Observation area (stand-alone only) ---
            AddSection("Observation", MyStrings.Observation);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyRemoteControl>(IdPrefix + "Checkbox_ObservationAtGps");
                c.Title = MyStrings.ObservationAtGps;
                c.Tooltip = MyStrings.ObservationAtGpsTip;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Visible = CustomVisibleCondition;
                c.Enabled = ObservationCondition;
                c.Getter = (b) => { var l = GetBlock(b); return l != null && l.Terminal_ObservationAtGps; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_ObservationAtGps = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddListbox("Listbox_ObservationGps", MyStrings.ObservationGps, MyStrings.ObservationGpsTip, 5, false,
                ObservationGpsContent, ObservationGpsSelected, ObservationGpsCondition);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_ObservationAreaDraw");
                c.Title = MyStrings.ShowObservationArea;
                c.Tooltip = MyStrings.ShowObservationAreaTip;
                c.OnText = MyStrings.On;
                c.OffText = MyStrings.Off;
                c.Visible = CustomVisibleCondition;
                c.Enabled = ObservationCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_ObservationAreaDraw ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_ObservationAreaDraw = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            AddObservationSlider("Slider_ObservationSizeX", MyStrings.AreaWidth, MyStrings.BLANK, 0, true);
            AddObservationSlider("Slider_ObservationSizeY", MyStrings.AreaHeight, MyStrings.BLANK, 1, true);
            AddObservationSlider("Slider_ObservationSizeZ", MyStrings.AreaDepth, MyStrings.BLANK, 2, true);
            AddObservationSlider("Slider_ObservationOffsetX", MyStrings.OffsetRight, MyStrings.BLANK, 0, false);
            AddObservationSlider("Slider_ObservationOffsetY", MyStrings.OffsetUp, MyStrings.BLANK, 1, false);
            AddObservationSlider("Slider_ObservationOffsetZ", MyStrings.OffsetForward, MyStrings.BLANK, 2, false);

            // --- Welder ---
            AddSection("Welder", MyStrings.Welder);
            AddWorkModeCheckbox("Checkbox_WeldUnfinished", MyStrings.WeldUnfinished,
                MyStrings.BLANK, Construction.WorkModes.WeldUnfinishedBlocks);
            AddWorkModeCheckbox("Checkbox_RepairDamaged", MyStrings.RepairDamaged,
                MyStrings.BLANK, Construction.WorkModes.RepairDamagedBlocks);
            AddWorkModeCheckbox("Checkbox_WeldProjected", MyStrings.WeldProjected,
                MyStrings.BLANK, Construction.WorkModes.WeldProjectedBlocks);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyRemoteControl>(IdPrefix + "Combo_WorkPriority");
                c.Title = MyStrings.WeldPriority;
                c.Tooltip = MyStrings.BLANK;
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.ComboBoxContent = Construction.WorkPriorities.ComboContent;
                c.Getter = (b) => GetBlock(b)?.Terminal_ConstructionWorkPriority ?? 0;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_ConstructionWorkPriority = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddColor("Color_WeldIgnoreColor", MyStrings.WeldIgnoreColor,
                MyStrings.WeldIgnoreColorTip,
                l => l.Terminal_WeldIgnoreColor, (l, v) => l.Terminal_WeldIgnoreColor = v);

            // --- Grinder ---
            AddSection("Grinder", MyStrings.Grinder);
            AddWorkModeCheckbox("Checkbox_Grind", MyStrings.Grind,
                MyStrings.BLANK, Construction.WorkModes.Grind);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlColor, IMyRemoteControl>(IdPrefix + "Color_GrindColor");
                c.Title = MyStrings.GrindColor;
                c.Tooltip = MyStrings.GrindColorTip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_GrindColor ?? Color.Red;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_GrindColor = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }

            // --- Mining: ore survey, ores this drone won't mine ---
            AddSection("Mining", MyStrings.Mining);
            AddCheckbox("Checkbox_SurveyForOres", MyStrings.SurveyForOres,
                MyStrings.BLANK,
                l => l.Terminal_SurveyForOres, (l, v) => l.Terminal_SurveyForOres = v);

            AddIgnoreLists(false);

            // --- Farming (not implemented yet): crops this drone won't farm ---
            AddSection("Farming", MyStrings.Farming);
            AddIgnoreLists(true);

            // --- Combat handover (any drone can be set up; only Combat drone profiles take combat jobs / launch) ---
            AddSection("Combat", MyStrings.Combat);
            AddSlider("Slider_CombatHandoverDistance", MyStrings.CombatHandoverDistance,
                MyStrings.CombatHandoverDistanceTip,
                DroneControllerBlock.COMBAT_HANDOVER_MIN, DroneControllerBlock.COMBAT_HANDOVER_MAX,
                l => l.Terminal_CombatHandoverDistance, (l, v) => l.Terminal_CombatHandoverDistance = v,
                (l, sb) => sb.AppendLoc(MyStrings.Metres, l.Terminal_CombatHandoverDistance.ToString("F0")));
            AddSlider("Slider_CombatNoTargetSeconds", MyStrings.CombatNoTargetSeconds,
                MyStrings.CombatNoTargetSecondsTip,
                DroneControllerBlock.COMBAT_NO_TARGET_MIN, DroneControllerBlock.COMBAT_NO_TARGET_MAX,
                l => l.Terminal_CombatNoTargetSeconds, (l, v) => l.Terminal_CombatNoTargetSeconds = v,
                (l, sb) => sb.AppendLoc(MyStrings.Seconds, l.Terminal_CombatNoTargetSeconds.ToString("F0")));
            AddSlider("Slider_AutoLaunchRadius", MyStrings.AutoLaunchRadius,
                MyStrings.AutoLaunchRadiusTip,
                0f, DroneControllerBlock.AUTO_LAUNCH_RADIUS_MAX,
                l => l.Terminal_AutoLaunchRadius, (l, v) => l.Terminal_AutoLaunchRadius = v,
                (l, sb) => { if (l.Terminal_AutoLaunchRadius <= 0) sb.AppendLoc(MyStrings.Off); else sb.AppendLoc(MyStrings.Metres, l.Terminal_AutoLaunchRadius.ToString("F0")); });
            AddSlider("Slider_EscortFollowDistance", MyStrings.EscortFollowDistance,
                MyStrings.BLANK,
                DroneControllerBlock.ESCORT_FOLLOW_MIN, DroneControllerBlock.ESCORT_FOLLOW_MAX,
                l => l.Terminal_EscortFollowDistance, (l, v) => l.Terminal_EscortFollowDistance = v,
                (l, sb) => sb.AppendLoc(MyStrings.Metres, l.Terminal_EscortFollowDistance.ToString("F0")));
            AddCheckbox("Checkbox_CombatRetreatOnFlee", MyStrings.CombatRetreatOnFlee,
                MyStrings.CombatRetreatOnFleeTip,
                l => l.Terminal_CombatRetreatOnFlee, (l, v) => l.Terminal_CombatRetreatOnFlee = v);
            AddCheckbox("Checkbox_CombatRetreatOnLowPower", MyStrings.CombatRetreatOnLowPower,
                MyStrings.CombatRetreatOnLowPowerTip,
                l => l.Terminal_CombatRetreatOnLowPower, (l, v) => l.Terminal_CombatRetreatOnLowPower = v);

            // --- Jobs (any mode but "managed by player") ---
            AddSection("Jobs", MyStrings.Jobs);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_TaskTimeout");
                c.Title = MyStrings.TaskTimeout;
                c.Tooltip = MyStrings.TaskTimeoutTip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.SetLimits(b => 0f, b => DroneControllerBlock.MaxTaskTimeoutSeconds);
                c.Getter = (b) => GetBlock(b)?.Terminal_TaskTimeoutSeconds ?? 30f;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_TaskTimeoutSeconds = v; };
                c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) sb.AppendLoc(MyStrings.Seconds, (int)l.Terminal_TaskTimeoutSeconds); };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyRemoteControl>(IdPrefix + "Slider_LocalJobRadius");
                c.Title = MyStrings.LocalJobRadius;
                c.Tooltip = MyStrings.LocalJobRadiusTip;
                c.Visible = CustomVisibleCondition;
                c.Enabled = JobsCondition;
                c.SetLimits(DroneControllerBlock.LOCAL_JOB_RADIUS_MIN, DroneControllerBlock.LOCAL_JOB_RADIUS_MAX);
                c.Getter = (b) => GetBlock(b)?.Terminal_LocalJobRadius ?? 15000f;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_LocalJobRadius = v; };
                c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) sb.AppendLoc(MyStrings.Metres, l.Terminal_LocalJobRadius.ToString("F0")); };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddLabel("Label_ExcludedCapabilities", MyStrings.ExcludedCapabilities);
            AddListbox("Listbox_Capabilities", MyStrings.Capabilities,
                MyStrings.CapabilitiesTip,
                5, true, (l, items, sel) => l.Terminal_CapabilityListContent(items, sel), (l, sel) => l.Terminal_SelectCapabilities(sel), JobsCondition, true);
            AddListbox("Listbox_ExcludedCapabilities", MyStrings.Excluded,
                MyStrings.ExcludedCapablitiesTip,
                4, true, (l, items, sel) => l.Terminal_ExcludedListContent(items, sel), (l, sel) => l.Terminal_SelectExclusions(sel), JobsCondition, true);
            AddButton("Button_ExcludeSelected", MyStrings.ExcludeSelected,
                MyStrings.BLANK, l => l.Terminal_ExcludeSelected(), JobsCondition, true);
            AddButton("Button_RemoveSelectedExclusions", MyStrings.RemoveSelected,
                MyStrings.BLANK, l => l.Terminal_RemoveSelectedExclusions(), JobsCondition, true);
            AddButton("Button_ClearExclusions", MyStrings.ClearAll,
                MyStrings.BLANK, l => l.Terminal_ClearExclusions(), JobsCondition, true);

            // --- Debug: views (any mode) and orders ("managed by player" only) ---
            AddSection("Debug", MyStrings.Debug);
            {
                // Always enabled: a view, not an order (any operation mode, AI on or off)
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyRemoteControl>(IdPrefix + "OnOff_DrawNavigationTargets");
                c.Title = MyStrings.DrawNavigationTargets;
                c.Tooltip = MyStrings.BLANK;
                c.OnText = MyStrings.On;
                c.OffText = MyStrings.Off;
                c.Visible = CustomVisibleCondition;
                c.Getter = (b) => GetBlock(b)?.Terminal_DrawNavigationTargets ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_DrawNavigationTargets = v; };
                c.SupportsMultipleBlocks = true;
                MyAPIGateway.TerminalControls.AddControl<IMyRemoteControl>(c);
            }
            AddButton("Button_Debug_Log", MyStrings.DebugLog, MyStrings.BLANK, l => l.Terminal_Debug_Log());
            AddTextbox("Textbox_Debug_SetOrientationTargetInput", MyStrings.SetOrientationTargetInput,
                MyStrings.BLANK,
                l => l.Terminal_Debug_SetOrientationTargetInput, (l, v) => l.Terminal_Debug_SetOrientationTargetInput = v, DebugCondition);
            AddButton("Button_Debug_SetOrientationTargetAction", MyStrings.SetOrientationTargetAction, MyStrings.BLANK,
                l => l.Terminal_Debug_SetOrientationTarget(), DebugCondition);
            AddTextbox("Textbox_Debug_NavigationTargetGPS", MyStrings.NavigationTargetGPS, MyStrings.BLANK,
                l => l.Textbox_Debug_NavigationTargetGPS, (l, v) => l.Textbox_Debug_NavigationTargetGPS = v, DebugCondition);
            AddTextbox("Textbox_Debug_NavigationTargetGPSApproachFrom", MyStrings.NavigationTargetGPSApproachFrom, MyStrings.BLANK,
                l => l.Textbox_Debug_NavigationTargetGPSApproachFrom, (l, v) => l.Textbox_Debug_NavigationTargetGPSApproachFrom = v, DebugCondition);
            AddButton("Button_Debug_NavigationTargetGPSActionTrigger", MyStrings.NavigationTargetGPSActionTrigger, MyStrings.BLANK,
                l => l.Terminal_Debug_NavigateToTarget(), DebugCondition);
            AddButton("Button_Debug_QueueWaypoint", MyStrings.QueueWaypoint,
                MyStrings.BLANK,
                l => l.Terminal_Debug_QueueWaypoint(), DebugCondition);
            AddListbox("Listbox_Debug_Mount", MyStrings.Mount,
                MyStrings.BLANK,
                4, false, (l, items, sel) => l.Terminal_Debug_MountListContent(items, sel), (l, sel) => l.Terminal_Debug_SelectMount(sel), DebugCondition);
            AddTextbox("Textbox_Debug_PlaceMountTargetGPS", MyStrings.PlaceMountTargetGPS, MyStrings.BLANK,
                l => l.Textbox_Debug_PlaceMountTargetGPS, (l, v) => l.Textbox_Debug_PlaceMountTargetGPS = v, DebugCondition);
            AddButton("Button_Debug_PlaceMount", MyStrings.PlaceMount, MyStrings.BLANK,
                l => l.Terminal_Debug_PlaceMount(), DebugCondition);
            AddListbox("Listbox_Debug_Anchor", MyStrings.Anchor,
                MyStrings.BLANK,
                4, false, (l, items, sel) => l.Terminal_Debug_AnchorListContent(items, sel), (l, sel) => l.Terminal_Debug_SelectAnchor(sel), DebugCondition);
            AddTextbox("Textbox_Debug_RelativeOffset", MyStrings.RelativeOffset, MyStrings.BLANK,
                l => l.Textbox_Debug_RelativeOffset, (l, v) => l.Textbox_Debug_RelativeOffset = v, DebugCondition);
            AddButton("Button_Debug_NavigateRelative", MyStrings.NavigateRelative, MyStrings.BLANK,
                l => l.Terminal_Debug_NavigateRelative(), DebugCondition);
        }
    }
}
