using System;
using System.Collections.Generic;
using System.Globalization;

using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

using Automata.Util;
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
        #region UI strings
        private static readonly MyStringId StrKg = MyStringId.GetOrCompute("{0} kg");
        private static readonly MyStringId StrBroadcastSends = MyStringId.GetOrCompute("nothing yet.");
        private static readonly MyStringId StrCheckboxShowGps = MyStringId.GetOrCompute("Show deposits as GPS");
        private static readonly MyStringId StrCheckboxShowGpsTip = MyStringId.GetOrCompute("Temporary GPS markers for the known deposits (nearest first), for the block's owner only.");
        private static readonly MyStringId StrShowDeposits = MyStringId.GetOrCompute("Show known deposits");
        private static readonly MyStringId StrShowDepositsTip = MyStringId.GetOrCompute("Puts the known deposits (nearest first) on your GPS list as temporary markers, once. The game drops them after a few minutes; pressing again replaces them.");
        private static readonly MyStringId StrCheckboxKeepConstant = MyStringId.GetOrCompute("Keep orders constant");
        private static readonly MyStringId StrCheckboxKeepConstantTip = MyStringId.GetOrCompute("Orders are not counted down: mining jobs keep going out while deposits of the ore are known.");
        private static readonly MyStringId StrOrderAdd = MyStringId.GetOrCompute("Add / update");
        private static readonly MyStringId StrOrderAddTip = MyStringId.GetOrCompute("The order goes into the list (replacing the same ore's order).");
        private static readonly MyStringId StrOrderEdit = MyStringId.GetOrCompute("Edit");
        private static readonly MyStringId StrOrderEdit2Tip = MyStringId.GetOrCompute("The order replaces the one selected in the list.");
        private static readonly MyStringId StrOrderRemove = MyStringId.GetOrCompute("Remove");
        private static readonly MyStringId StrOrderRemoveTip = MyStringId.GetOrCompute("Removes the order selected in the list.");
        private static readonly MyStringId StrOrderClear = MyStringId.GetOrCompute("Clear all");
        private static readonly MyStringId StrOrderClearTip = MyStringId.GetOrCompute("Empties the order list.");
        private static readonly MyStringId StrRetryJobs = MyStringId.GetOrCompute("Retry jobs");
        private static readonly MyStringId StrRetryJobsTip = MyStringId.GetOrCompute("Mining jobs no drone has taken are announced again now, holds after failed auctions lifted.");
        private static readonly MyStringId StrKnownOresOreDeposits = MyStringId.GetOrCompute("Known ores (ore:deposits)");
        private static readonly MyStringId StrOrdersOreKg = MyStringId.GetOrCompute("Orders (ore:kg)");
        private static readonly MyStringId StrEnableThisMiningSurveyorOre = MyStringId.GetOrCompute("Enable this Mining Surveyor: ore scans, survey data from drones, mining jobs.");
        private static readonly MyStringId StrJobSize = MyStringId.GetOrCompute("Job size");
        private static readonly MyStringId StrJobSizeTip = MyStringId.GetOrCompute("kg of ore one mining job asks for. A drone fills its cargo; what it brings is counted off the order.");
        private static readonly MyStringId StrKnownOres = MyStringId.GetOrCompute("Known ores");
        private static readonly MyStringId StrKnownOresTip = MyStringId.GetOrCompute("Ores of the deposits this surveyor knows, without an order yet. Select one to order it below.");
        private static readonly MyStringId StrOrders = MyStringId.GetOrCompute("Orders");
        private static readonly MyStringId StrOrdersTip = MyStringId.GetOrCompute("Ore to mine, kg. Each mining job is counted off its order; at 0 no more jobs go out (unless kept constant).");
        private static readonly MyStringId StrOrder = MyStringId.GetOrCompute("Order");
        private static readonly MyStringId StrOrderTip = MyStringId.GetOrCompute("ore:kg, e.g. Ore/Iron:100000 or Iron:50000 (whole kg above 0)");
        #endregion

        const string IdPrefix = AutomataSession.MOD_NAME + "_MiningSurveyor_";
        static bool Done = false;
        static IMyTerminalControlListbox oreList, orderList;
        static IMyTerminalControlTextbox editBox;

        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;
            AutomataProgrammableBlocks.HideVanillaOnce();
            CreateControls();
            Log.Info("Created MS terminal controls");
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
                c.Tooltip = StrEnableThisMiningSurveyorOre;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Getter = b => GetBlock(b)?.Terminal_IsEnabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_IsEnabled = v; };
                Add(c);
            }
            Checkbox("CheckboxShowGps", StrCheckboxShowGps,
                     StrCheckboxShowGpsTip,
                     l => l.Terminal_ShowGps, (l, v) => l.Terminal_ShowGps = v);
            Button("ShowDeposits", StrShowDeposits, StrShowDepositsTip, l => l.Terminal_ShowDeposits());
            Checkbox("CheckboxKeepConstant", StrCheckboxKeepConstant,
                     StrCheckboxKeepConstantTip,
                     l => l.Terminal_KeepOrdersConstant, (l, v) => l.Terminal_KeepOrdersConstant = v);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, IMyProgrammableBlock>(IdPrefix + "SliderJobSize");
                c.Title = StrJobSize;
                c.Tooltip = StrJobSizeTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = Visible;
                c.SetLimits(MiningSurveyorBlock.JOB_SIZE_MIN, MiningSurveyorBlock.JOB_SIZE_MAX);
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_JobSizeKg : MiningSurveyorBlock.JOB_SIZE_MIN; };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_JobSizeKg = v; };
                c.Writer = (b, sb) => { var l = GetBlock(b); if (l != null) sb.AppendLoc(StrKg, l.Terminal_JobSizeKg.ToString("N0", CultureInfo.InvariantCulture)); };
                Add(c);
            }
            Separator();
            Label(IdPrefix + "LabelOres", StrKnownOresOreDeposits);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "OreList");
                c.Title = StrKnownOres;
                c.Tooltip = StrKnownOresTip;
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
            Label(IdPrefix + "LabelOrders", StrOrdersOreKg);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "OrderList");
                c.Title = StrOrders;
                c.Tooltip = StrOrdersTip;
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
                c.Title = StrOrder;
                c.Tooltip = StrOrderTip;
                c.SupportsMultipleBlocks = false;
                c.Visible = Visible;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_EditText : new System.Text.StringBuilder(); };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null && !ReferenceEquals(v, l.Terminal_EditText)) l.Terminal_EditText.Clear().Append(v); };
                editBox = c;
                Add(c);
            }
            Button("OrderAdd", StrOrderAdd, StrOrderAddTip, l => l.Terminal_AddOrder());
            Button("OrderEdit2", StrOrderEdit, StrOrderEdit2Tip, l => l.Terminal_EditOrder());
            Button("OrderRemove", StrOrderRemove, StrOrderRemoveTip, l => l.Terminal_RemoveOrder());
            Button("OrderClear", StrOrderClear, StrOrderClearTip, l => { l.Terminal_ClearOrders(); return true; });
            Button("RetryJobs", StrRetryJobs, StrRetryJobsTip, l => l.Terminal_RetryJobs());
            NicknameControl.Add<IMyProgrammableBlock, MiningSurveyorBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[MiningSurveyor]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), true);
            BroadcastControllerControl.Add(IdPrefix + "UseBroadcastController", GetBlock, l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v, StrBroadcastSends);
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
