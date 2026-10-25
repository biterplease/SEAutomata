using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

using Automata.Util;
using Automata.Util.Strings;
using Automata.Util.Logging;

namespace Automata.Mining
{
    /// <summary>
    /// Mining surveyor terminal: on / off, GPS markers, job size, "keep orders constant", and the ore orders. Top: the
    /// ores of known deposits without an order yet. Bottom: the orders ("Ore/Iron:100000", kg). Selecting either puts
    /// it in the edit box; the buttons apply the edit box.
    /// </summary>
    public static class MiningSurveyorTerminalControls
    {

        const string IdPrefix = AutomataSession.MOD_NAME + "_MiningSurveyor_";
        static bool Done = false;
        static IMyTerminalControlListbox oreList, orderList;
        static IMyTerminalControlTextbox editBox;

        const string ICON_TOGGLE = @"Textures\GUI\Icons\Actions\Toggle.dds";
        const string ICON_ON = @"Textures\GUI\Icons\Actions\SwitchOn.dds";
        const string ICON_OFF = @"Textures\GUI\Icons\Actions\SwitchOff.dds";
        const string ICON_RESET = @"Textures\GUI\Icons\Actions\Reset.dds";

        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;
            AutomataProgrammableBlocks.HideVanillaOnce();
            CreateControls();
            CreateActions();
            if (Log.DebugEnabled) Log.Debug("Created MS terminal controls");
        }

        static void AddAction(string id, string name, string icon, Action<MiningSurveyorBlock> action, Action<MiningSurveyorBlock, StringBuilder> writer)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyProgrammableBlock>(IdPrefix + id);
            a.Name = new StringBuilder(name);
            a.ValidForGroups = true;
            a.Icon = icon;
            a.Action = (b) => { var l = GetBlock(b); if (l != null) action(l); };
            a.Writer = (b, sb) => { var l = GetBlock(b); if (l != null && writer != null) writer(l, sb); };
            a.Enabled = Visible;
            MyAPIGateway.TerminalControls.AddAction<IMyProgrammableBlock>(a);
        }

        static void CreateActions()
        {
            AddAction(IdPrefix + "_Action_ShowKnownOreDeposits_ON", Loc.T(MyStrings.Mining_ToggleShowKnownOreDepositsOn), ICON_ON, b => { if (!b.Terminal_ShowGps) { b.Terminal_ShowGps = true; } }, (b, sb) => sb.Append(Loc.T(MyStrings.Mining_ShowOn)));
            AddAction(IdPrefix + "_Action_ShowKnownOreDeposits_OFF", Loc.T(MyStrings.Mining_ToggleShowKnownOreDepositsOff), ICON_OFF, b => { if (b.Terminal_ShowGps) { b.Terminal_ShowGps = false; } }, (b, sb) => sb.Append(Loc.T(MyStrings.Mining_ShowOff)));
            AddAction(IdPrefix + "_Action_ShowKnownOreDeposits_ON_OFF", Loc.T(MyStrings.Mining_ToggleShowKnownOreDepositsOnOff), ICON_TOGGLE, b => { b.Terminal_ShowGps = !b.Terminal_ShowGps; }, (b, sb) => sb.Append(b.Terminal_IsEnabled ? Loc.T(MyStrings.Mining_ShowOn) : Loc.T(MyStrings.Mining_ShowOff)));
            
            AddAction(IdPrefix + "_Action_DeleteDeposits", Loc.T(MyStrings.Mining_DeleteKnownDeposits), ICON_RESET, b => { b.Terminal_DeleteDeposits(); }, null);
        }
        static MiningSurveyorBlock GetBlock(IMyTerminalBlock b)
        {
            return b != null && b.GameLogic != null ? b.GameLogic.GetAs<MiningSurveyorBlock>() : null;
        }

        static bool Visible(IMyTerminalBlock b)
        {
            return GetBlock(b) != null;
        }

        static void Add(IMyTerminalControl c)
        {
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
        }

        static void Refresh()
        {
            if (oreList != null) oreList.UpdateVisual();
            if (orderList != null) orderList.UpdateVisual();
            if (editBox != null) editBox.UpdateVisual();
        }

        static void Separator()
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            Add(c);
        }

        static void Label(string id, MyStringId text)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyProgrammableBlock>(id);
            c.Label = text;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            Add(c);
        }

        static void Checkbox(string id, MyStringId title, MyStringId tooltip, Func<MiningSurveyorBlock, bool> get, Action<MiningSurveyorBlock, bool> set)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = Visible;
            c.Getter = b => { var l = GetBlock(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            Add(c);
        }

        /// <summary>
        /// On/Off Toggle
        /// </summary>
        static void OnOffSwitch(string id, MyStringId title, MyStringId tooltip, Func<MiningSurveyorBlock, bool> get, Action<MiningSurveyorBlock, bool> set)
        {
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(id);
                c.Title = title;
                c.Tooltip = tooltip;
                c.SupportsMultipleBlocks = true;

                c.Visible = Visible;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;

                c.Getter = b => { var l = GetBlock(b); return l != null && get(l); };
                c.Setter =(b, v) => { var l = GetBlock(b); if (l != null) set(l,v); };
                Add(c);
            }
        }

        static void Button(string id, MyStringId title, MyStringId tooltip, Func<MiningSurveyorBlock, bool> act)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = false;
            c.Visible = Visible;
            c.Action = b => { var l = GetBlock(b); if (l != null && act(l)) Refresh(); };
            Add(c);
        }

        public static void CreateControls()
        {
            Separator();
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOffEnabled");
                c.Title = MySpaceTexts.BlockPropertyTitle_EnableAI;
                c.Tooltip = MyStrings.Mining_EnableThisMiningSurveyorOre;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Getter = b => GetBlock(b)?.Terminal_IsEnabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_IsEnabled = v; };
                Add(c);
            }
            Button("RetryJobs", MyStrings.Mining_RetryJobs, MyStrings.Mining_MiningJobsNoDroneHasTooltip, l => l.Terminal_RetryJobs());
            Button("DeleteDeposits", MyStrings.Mining_DeleteKnownDeposits, MyStrings.Mining_DeleteKnownDepositsTooltip, l => l.Terminal_DeleteDeposits());
            OnOffSwitch("CheckboxShowGps", MyStrings.Mining_ShowDepositsAsGPS,
                     MyStrings.Mining_ShowDepositsAsGPSTooltip,
                     l => l.Terminal_ShowGps, (l, v) => l.Terminal_ShowGps = v);
            Checkbox("CheckboxKeepConstant", MyStrings.Mining_KeepOrdersConstant,
                     MyStrings.Mining_KeepOrdersConstantTooltip,
                     l => l.Terminal_KeepOrdersConstant, (l, v) => l.Terminal_KeepOrdersConstant = v);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + "SliderJobSize");
                c.Title = MyStrings.Mining_JobSize;
                c.Tooltip = MyStrings.Mining_JobSizeTooltip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.SetLimits(MiningSurveyorBlock.JOB_SIZE_MIN, MiningSurveyorBlock.JOB_SIZE_MAX);
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_JobSizeKg : MiningSurveyorBlock.JOB_SIZE_MIN; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_JobSizeKg = v; };
                c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) sb.AppendLoc(MyStrings.Mining_Arg0Kg, l.Terminal_JobSizeKg.ToString("N0", CultureInfo.InvariantCulture)); };
                Add(c);
            }
            Separator();
            Label(IdPrefix + "LabelOres", MyStrings.Mining_KnownOresOreDeposits);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "OreList");
                c.Title = MyStrings.Mining_KnownOres;
                c.Tooltip = MyStrings.Mining_KnownOresTooltip;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Multiselect = false;
                c.VisibleRowsCount = 6;
                c.ListContent = OreListContent;
                c.ItemSelected = (b, selected) =>
                {
                    var l = GetBlock(b);
                    if (l == null || selected.Count == 0 || !(selected[0].UserData is MyStringHash)) return;
                    l.Terminal_SelectOre((MyStringHash)selected[0].UserData);
                    Refresh();
                };
                oreList = c;
                Add(c);
            }
            Label(IdPrefix + "LabelOrders", MyStrings.Mining_OrdersOreKg);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "OrderList");
                c.Title = MyStrings.Mining_Orders;
                c.Tooltip = MyStrings.Mining_OrdersTooltip;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Multiselect = false;
                c.VisibleRowsCount = 6;
                c.ListContent = OrderListContent;
                c.ItemSelected = (b, selected) =>
                {
                    var l = GetBlock(b);
                    if (l == null || selected.Count == 0 || !(selected[0].UserData is int)) return;
                    l.Terminal_SelectOrder((int)selected[0].UserData);
                    Refresh();
                };
                orderList = c;
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyProgrammableBlock>(IdPrefix + "OrderEdit");
                c.Title = MyStrings.Mining_Order;
                c.Tooltip = MyStrings.Mining_OrderTooltip;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_EditText : new System.Text.StringBuilder(); };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null && !ReferenceEquals(v, l.Terminal_EditText)) l.Terminal_EditText.Clear().Append(v); };
                editBox = c;
                Add(c);
            }
            Button("OrderAdd", MyStrings.Mining_AddUpdate, MyStrings.Mining_AddUpdateTooltip, l => l.Terminal_AddOrder());
            Button("OrderEdit2", MyStrings.Keen_Edit, MyStrings.Mining_TheOrderReplacesTheOneTooltip, l => l.Terminal_EditOrder());
            Button("OrderRemove", MyStrings.Keen_Remove, MyStrings.Mining_RemoveTooltip, l => l.Terminal_RemoveOrder());
            Button("OrderClear", MyStrings.Mining_ClearAll, MyStrings.Mining_ClearAllTooltip, l => { l.Terminal_ClearOrders(); return true; });
            NicknameControl.Add<IMyProgrammableBlock, MiningSurveyorBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[MiningSurveyor]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), true);
        }

        static void OreListContent(IMyTerminalBlock b, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var l = GetBlock(b);
            if (l == null) return;
            var ordered = l.Terminal_OrderKeys();
            var known = l.Terminal_KnownOres;
            for (int i = 0; i < known.Count; i++)
            {
                int c = known[i].LastIndexOf(':');
                if (c <= 0) continue;
                var key = MyStringHash.GetOrCompute(known[i].Substring(0, c));
                if (ordered.Contains(key)) continue;
                ComponentData data;
                string name = AutomataSession.componentLookup.TryGetValue(key, out data) ? data.DisplayName : key.String;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(known[i]),
                                                            MyStringId.GetOrCompute(name + ": " + known[i].Substring(c + 1) + " deposit(s)"), key);
                items.Add(item);
                if (key == l.Terminal_SelectedOre) selected.Add(item);
            }
        }

        static void OrderListContent(IMyTerminalBlock b, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var l = GetBlock(b);
            if (l == null) return;
            var orders = l.Terminal_Orders;
            for (int i = 0; i < orders.Count; i++)
            {
                var text = MyStringId.GetOrCompute(orders[i]);
                var item = new MyTerminalControlListBoxItem(text, text, i);
                items.Add(item);
                if (i == l.Terminal_SelectedOrder) selected.Add(item);
            }
        }
    }
}
