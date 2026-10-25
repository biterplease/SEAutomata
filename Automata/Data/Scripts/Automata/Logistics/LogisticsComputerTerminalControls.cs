using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.Game.Localization;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;

using Automata.Config;
using Automata.Util;
using Automata.Util.Logging;
using Automata.Util.Strings;

namespace Automata.Logistics
{
    /// <summary>
    /// Logistics computer terminal: on / off, work modes and the Min / Max lists. Top: every item (components,
    /// tools, player weapons, their ammo, block ammo, ingots, ores) without an entry yet, as "item:0:0". Bottom: the
    /// Min / Max entries. Selecting an entry puts it in the edit box; the buttons apply the edit box.
    /// </summary>
    public static class LogisticsComputerTerminalControls
    {

        public static ServerConfig.LogisticsComputerConfig BlockConfig = ServerConfig.Instance.LogisticsComputer;

        const string IdPrefix = AutomataSession.MOD_NAME + "_LogisticsComputer_";
        static bool Done = false;

        // Redrawn after a button / selection changes them
        static IMyTerminalControlListbox itemList, quotaList;
        static IMyTerminalControlTextbox editBox;

        public static void DoOnce(IMyModContext context)
        {
            if (Done) return;
            Done = true;
            AutomataProgrammableBlocks.HideVanillaOnce();
            CreateControls();
            if (Log.DebugEnabled) Log.Debug("Created LC terminal controls");
        }

        private static LogisticsComputerBlock GetBlock(IMyTerminalBlock block)
        {
            if (block != null && block.GameLogic != null) return block.GameLogic.GetAs<LogisticsComputerBlock>();
            return null;
        }

        static bool CustomVisibleCondition(IMyTerminalBlock b)
        {
            return b?.GameLogic?.GetAs<LogisticsComputerBlock>() != null;
        }

        static void Refresh()
        {
            if (itemList != null) itemList.UpdateVisual();
            if (quotaList != null) quotaList.UpdateVisual();
            if (editBox != null) editBox.UpdateVisual();
        }

        static void Add(IMyTerminalControl c)
        {
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
        }

        static void Separator()
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
            c.SupportsMultipleBlocks = true;
            c.Visible = CustomVisibleCondition;
            Add(c);
        }

        static void Label(string id, MyStringId text)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlLabel, IMyProgrammableBlock>(id);
            c.Label = text;
            c.SupportsMultipleBlocks = true;
            c.Visible = CustomVisibleCondition;
            Add(c);
        }

        static void Checkbox(string id, MyStringId title, MyStringId tooltip, Func<IMyTerminalBlock, bool> visible, Func<IMyTerminalBlock, bool> enabled,
                             Func<LogisticsComputerBlock, bool> get, Action<LogisticsComputerBlock, bool> set)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = true;
            c.Visible = visible;
            c.Enabled = enabled;
            c.Getter = b => { var l = GetBlock(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) set(l, v); };
            Add(c);
        }

        static void Button(string id, MyStringId title, MyStringId tooltip, Func<LogisticsComputerBlock, bool> act)
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlButton, IMyProgrammableBlock>(IdPrefix + id);
            c.Title = title;
            c.Tooltip = tooltip;
            c.SupportsMultipleBlocks = false;
            c.Visible = CustomVisibleCondition;
            c.Action = b =>
            {
                var l = GetBlock(b);
                if (l == null) return;
                if (act(l)) Refresh();
            };
            Add(c);
        }

        public static void CreateControls()
        {
            Separator();
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlOnOffSwitch, IMyProgrammableBlock>(IdPrefix + "OnOffEnabled");
                c.Title = MySpaceTexts.BlockPropertyTitle_EnableAI;
                c.Tooltip = MyStrings.Logistics_EnableThisLogisticsComputerAITooltip;
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Getter = b => GetBlock(b)?.Terminal_IsEnabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_IsEnabled = v; };
                Add(c);
            }
            Separator();
            Label("", MyStrings.Logistics_WorkModes);
            Checkbox("CheckboxLCOpModeProvideForLogistics", MyStrings.Logistics_ProvideForLogistics,
                     MyStrings.Logistics_ProvideForLogisticsTooltip,
                     b => CustomVisibleCondition(b) && BlockConfig.AllowLogistics, b => BlockConfig.AllowLogistics,
                     l => l.Terminal_WorkModeProvideForLogistics, (l, v) => l.Terminal_WorkModeProvideForLogistics = v);
            Checkbox("CheckboxLCOpModeProvideForConstruction", MyStrings.Logistics_ProvideForConstruction,
                     MyStrings.Logistics_ProvideForConstructionTooltip,
                     CustomVisibleCondition, b => BlockConfig.AllowLogistics,
                     l => l.Terminal_WorkModeProvideForConstruction, (l, v) => l.Terminal_WorkModeProvideForConstruction = v);
            Checkbox("CheckboxLCOpModePush", MyStrings.Logistics_PushAboveMax,
                     MyStrings.Logistics_PushAboveMaxTooltip,
                     b => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush,
                     b => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush,
                     l => l.Terminal_WorkModePush, (l, v) => l.Terminal_WorkModePush = v);
            Checkbox("CheckboxLCOpModeRequest", MyStrings.Logistics_RequestBelowMin,
                     MyStrings.Logistics_RequestBelowMinTooltip,
                     b => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests,
                     b => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests,
                     l => l.Terminal_WorkModeRequest, (l, v) => l.Terminal_WorkModeRequest = v);
            Checkbox("CheckboxLCAcceptOreWithoutRefinery", MyStrings.Logistics_AcceptOresIce,
                     MyStrings.Logistics_AcceptOresIceTooltip,
                     CustomVisibleCondition, b => BlockConfig.AllowLogistics,
                     l => l.Terminal_AcceptOreWithoutRefinery, (l, v) => l.Terminal_AcceptOreWithoutRefinery = v);
            Separator();
            Label(IdPrefix + "LabelItems", MyStrings.Logistics_ItemsItemMinMax);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "ItemList");
                c.Title = MyStrings.Logistics_Items;
                c.Tooltip = MyStrings.BLANK;
                c.SupportsMultipleBlocks = false;
                c.Visible = CustomVisibleCondition;
                c.Multiselect = false;
                c.VisibleRowsCount = 10;
                c.ListContent = ItemListContent;
                c.ItemSelected = (b, selected) =>
                {
                    var l = GetBlock(b);
                    if (l == null || selected.Count == 0 || !(selected[0].UserData is MyStringHash)) return;
                    l.Terminal_SelectItem((MyStringHash)selected[0].UserData);
                    Refresh();
                };
                itemList = c;
                Add(c);
            }
            Label(IdPrefix + "LabelQuotas", MyStrings.Logistics_MinMax0NotSet);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "QuotaList");
                c.Title = MyStrings.Logistics_MinMax;
                c.Tooltip = MyStrings.BLANK;
                c.SupportsMultipleBlocks = false;
                c.Visible = CustomVisibleCondition;
                c.Multiselect = false;
                c.VisibleRowsCount = 8;
                c.ListContent = QuotaListContent;
                c.ItemSelected = (b, selected) =>
                {
                    var l = GetBlock(b);
                    if (l == null || selected.Count == 0 || !(selected[0].UserData is int)) return;
                    l.Terminal_SelectQuota((int)selected[0].UserData);
                    Refresh();
                };
                quotaList = c;
                Add(c);
            }
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyProgrammableBlock>(IdPrefix + "QuotaEdit");
                c.Title = MyStrings.Logistics_Entry;
                c.Tooltip = MyStrings.Logistics_EntryTooltip;
                c.SupportsMultipleBlocks = false;
                c.Visible = CustomVisibleCondition;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_EditText : new StringBuilder(); };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null && !ReferenceEquals(v, l.Terminal_EditText)) l.Terminal_EditText.Clear().Append(v); };
                editBox = c;
                Add(c);
            }
            Button("QuotaEditAndAdd", MyStrings.Logistics_EditAndAdd, MyStrings.BLANK, l => l.Terminal_EditAndAdd());
            Button("QuotaEdit2", MyStrings.Keen_Edit, MyStrings.BLANK, l => l.Terminal_EditQuota());
            Button("QuotaRemove", MyStrings.Keen_Remove, MyStrings.BLANK, l => l.Terminal_RemoveQuota());
            Button("QuotaAddManually", MyStrings.Logistics_AddManually, MyStrings.BLANK, l => l.Terminal_AddManually());
            Button("QuotaClear", MyStrings.Logistics_ClearAll, MyStrings.BLANK, l => { l.Terminal_ClearQuotas(); return true; });
            Separator();
            Button("RetryHeld", MyStrings.Logistics_RetryHeldJobs, MyStrings.BLANK,
                   l => { l.Terminal_RetryHeld(); return false; });
            NicknameControl.Add<IMyProgrammableBlock, LogisticsComputerBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[LogisticComputer]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), true);
        }

        static readonly Dictionary<ItemCategory, MyStringId> CategoryNames = new Dictionary<ItemCategory, MyStringId>
        {
            { ItemCategory.Component, MyStrings.Logistics_Component },
            { ItemCategory.Tool, MyStrings.Logistics_Tool },
            { ItemCategory.PlayerWeapon, MyStrings.Logistics_PlayerWeapon },
            { ItemCategory.PlayerAmmo, MyStrings.Logistics_PlayerWeaponAmmo },
            { ItemCategory.BlockAmmo, MyStrings.Logistics_BlockAmmo },
            { ItemCategory.Ingot, MyStrings.Logistics_Ingot },
            { ItemCategory.Ore, MyStrings.Logistics_Ore },
            { ItemCategory.Other, MyStrings.Logistics_Other },
        };

        static void ItemListContent(IMyTerminalBlock b, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var l = GetBlock(b);
            if (l == null) return;
            var used = l.Terminal_QuotaKeys;
            var catalog = AutomataSession.itemCatalog;
            for (int i = 0; i < catalog.Count; i++)
            {
                var data = catalog[i];
                if (used.Contains(data.Key)) continue;
                MyStringId nameId;
                string name = CategoryNames.TryGetValue(data.Category, out nameId) ? Loc.T(nameId) : null;
                var item = new MyTerminalControlListBoxItem(MyStringId.GetOrCompute(QuotaEntry.Format(data.Key, 0, 0)),
                                                            MyStringId.GetOrCompute(data.DisplayName + " (" + name + ")"), data.Key);
                items.Add(item);
                if (data.Key == l.Terminal_SelectedItem) selected.Add(item);
            }
        }

        static void QuotaListContent(IMyTerminalBlock b, List<MyTerminalControlListBoxItem> items, List<MyTerminalControlListBoxItem> selected)
        {
            var l = GetBlock(b);
            if (l == null) return;
            var quotas = l.Terminal_Quotas;
            for (int i = 0; i < quotas.Count; i++)
            {
                var text = MyStringId.GetOrCompute(quotas[i]);
                var item = new MyTerminalControlListBoxItem(text, text, i);
                items.Add(item);
                if (i == l.Terminal_SelectedQuota) selected.Add(item);
            }
        }
    }
}
