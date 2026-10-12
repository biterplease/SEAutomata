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
        #region UI strings
        private static readonly MyStringId StrCategoryComponent = MyStringId.GetOrCompute("Component");
        private static readonly MyStringId StrCategoryTool = MyStringId.GetOrCompute("Tool");
        private static readonly MyStringId StrCategoryPlayerWeapon = MyStringId.GetOrCompute("Player weapon");
        private static readonly MyStringId StrCategoryPlayerAmmo = MyStringId.GetOrCompute("Player weapon ammo");
        private static readonly MyStringId StrCategoryBlockAmmo = MyStringId.GetOrCompute("Block ammo");
        private static readonly MyStringId StrCategoryIngot = MyStringId.GetOrCompute("Ingot");
        private static readonly MyStringId StrCategoryOre = MyStringId.GetOrCompute("Ore");
        private static readonly MyStringId StrCategoryOther = MyStringId.GetOrCompute("Other");
        private static readonly MyStringId StrModeProvideForLogistics = MyStringId.GetOrCompute("Provide for logistics");
        private static readonly MyStringId StrModeProvideForLogisticsTip = MyStringId.GetOrCompute("Offer this grid's items to other logistics computers' requests.");
        private static readonly MyStringId StrModeProvideForConstruction = MyStringId.GetOrCompute("Provide for construction");
        private static readonly MyStringId StrModeProvideForConstructionTip = MyStringId.GetOrCompute("Offer components for weld jobs, and volume to accept ground-block components.");
        private static readonly MyStringId StrModePush = MyStringId.GetOrCompute("Push (above Max)");
        private static readonly MyStringId StrModePushTip = MyStringId.GetOrCompute("Items above their Max are announced as surplus.");
        private static readonly MyStringId StrModeRequest = MyStringId.GetOrCompute("Request (below Min)");
        private static readonly MyStringId StrModeRequestTip = MyStringId.GetOrCompute("Items below their Min are requested.");
        private static readonly MyStringId StrAcceptOreWithoutRefinery = MyStringId.GetOrCompute("Accept ore from mining without a refinery");
        private static readonly MyStringId StrAcceptOreWithoutRefineryTip = MyStringId.GetOrCompute("Bid volume for mined ore even when no refinery here refines it.");
        private static readonly MyStringId StrQuotaEditAndAdd = MyStringId.GetOrCompute("Edit and add");
        private static readonly MyStringId StrQuotaEdit = MyStringId.GetOrCompute("Edit");
        private static readonly MyStringId StrQuotaRemove = MyStringId.GetOrCompute("Remove");
        private static readonly MyStringId StrQuotaAddManually = MyStringId.GetOrCompute("Add manually");
        private static readonly MyStringId StrQuotaClear = MyStringId.GetOrCompute("Clear all");
        private static readonly MyStringId StrRetryHeld = MyStringId.GetOrCompute("Retry held jobs");
        private static readonly MyStringId StrWorkModes = MyStringId.GetOrCompute("Work modes");
        private static readonly MyStringId StrItemsItemMinMax = MyStringId.GetOrCompute("Items (item:min:max)");
        private static readonly MyStringId StrMinMax0NotSet = MyStringId.GetOrCompute("Min / Max (0 = not set)");
        private static readonly MyStringId StrEnableTip = MyStringId.GetOrCompute("Enable this Logistics Computer AI behaviour.");
        private static readonly MyStringId StrItems = MyStringId.GetOrCompute("Items");
        private static readonly MyStringId StrMinMax = MyStringId.GetOrCompute("Min / Max");
        private static readonly MyStringId StrEntry = MyStringId.GetOrCompute("Entry");
        private static readonly MyStringId StrEntryTip = MyStringId.GetOrCompute("item:min:max, e.g. SteelPlate:100:5000 or Ingot/Iron:0:20000");
        #endregion

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
            Log.Info("Created LC terminal controls");
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
                c.Tooltip = StrEnableTip;
                c.SupportsMultipleBlocks = true;
                c.Visible = CustomVisibleCondition;
                c.OnText = MySpaceTexts.SwitchText_On;
                c.OffText = MySpaceTexts.SwitchText_Off;
                c.Getter = b => GetBlock(b)?.Terminal_IsEnabled ?? false;
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_IsEnabled = v; };
                Add(c);
            }
            Separator();
            Label("", StrWorkModes);
            Checkbox("CheckboxLCOpModeProvideForLogistics", StrModeProvideForLogistics,
                     StrModeProvideForLogisticsTip,
                     b => CustomVisibleCondition(b) && BlockConfig.AllowLogistics, b => BlockConfig.AllowLogistics,
                     l => l.Terminal_WorkModeProvideForLogistics, (l, v) => l.Terminal_WorkModeProvideForLogistics = v);
            Checkbox("CheckboxLCOpModeProvideForConstruction", StrModeProvideForConstruction,
                     StrModeProvideForConstructionTip,
                     CustomVisibleCondition, b => BlockConfig.AllowLogistics,
                     l => l.Terminal_WorkModeProvideForConstruction, (l, v) => l.Terminal_WorkModeProvideForConstruction = v);
            Checkbox("CheckboxLCOpModePush", StrModePush,
                     StrModePushTip,
                     b => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush,
                     b => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkPush,
                     l => l.Terminal_WorkModePush, (l, v) => l.Terminal_WorkModePush = v);
            Checkbox("CheckboxLCOpModeRequest", StrModeRequest,
                     StrModeRequestTip,
                     b => CustomVisibleCondition(b) && BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests,
                     b => BlockConfig.AllowLogistics && BlockConfig.AllowNetworkRequests,
                     l => l.Terminal_WorkModeRequest, (l, v) => l.Terminal_WorkModeRequest = v);
            Checkbox("CheckboxLCAcceptOreWithoutRefinery", StrAcceptOreWithoutRefinery,
                     StrAcceptOreWithoutRefineryTip,
                     CustomVisibleCondition, b => BlockConfig.AllowLogistics,
                     l => l.Terminal_AcceptOreWithoutRefinery, (l, v) => l.Terminal_AcceptOreWithoutRefinery = v);
            Separator();
            Label(IdPrefix + "LabelItems", StrItemsItemMinMax);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "ItemList");
                c.Title = StrItems;
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
            Label(IdPrefix + "LabelQuotas", StrMinMax0NotSet);
            {
                var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlListbox, IMyProgrammableBlock>(IdPrefix + "QuotaList");
                c.Title = StrMinMax;
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
                c.Title = StrEntry;
                c.Tooltip = StrEntryTip;
                c.SupportsMultipleBlocks = false;
                c.Visible = CustomVisibleCondition;
                c.Getter = b => { var l = GetBlock(b); return l != null ? l.Terminal_EditText : new StringBuilder(); };
                c.Setter = (b, v) => { var l = GetBlock(b); if (l != null && !ReferenceEquals(v, l.Terminal_EditText)) l.Terminal_EditText.Clear().Append(v); };
                editBox = c;
                Add(c);
            }
            Button("QuotaEditAndAdd", StrQuotaEditAndAdd, MyStrings.BLANK, l => l.Terminal_EditAndAdd());
            Button("QuotaEdit2", StrQuotaEdit, MyStrings.BLANK, l => l.Terminal_EditQuota());
            Button("QuotaRemove", StrQuotaRemove, MyStrings.BLANK, l => l.Terminal_RemoveQuota());
            Button("QuotaAddManually", StrQuotaAddManually, MyStrings.BLANK, l => l.Terminal_AddManually());
            Button("QuotaClear", StrQuotaClear, MyStrings.BLANK, l => { l.Terminal_ClearQuotas(); return true; });
            Separator();
            Button("RetryHeld", StrRetryHeld, MyStrings.BLANK,
                   l => { l.Terminal_RetryHeld(); return false; });
            NicknameControl.Add<IMyProgrammableBlock, LogisticsComputerBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[LogisticComputer]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), true);
            BroadcastControllerControl.Add(IdPrefix + "UseBroadcastController", GetBlock, l => l.Terminal_UseBroadcastController, (l, v) => l.Terminal_UseBroadcastController = v);
        }

        static readonly Dictionary<ItemCategory, MyStringId> CategoryNames = new Dictionary<ItemCategory, MyStringId>
        {
            { ItemCategory.Component, StrCategoryComponent },
            { ItemCategory.Tool, StrCategoryTool },
            { ItemCategory.PlayerWeapon, StrCategoryPlayerWeapon },
            { ItemCategory.PlayerAmmo, StrCategoryPlayerAmmo },
            { ItemCategory.BlockAmmo, StrCategoryBlockAmmo },
            { ItemCategory.Ingot, StrCategoryIngot },
            { ItemCategory.Ore, StrCategoryOre },
            { ItemCategory.Other, StrCategoryOther },
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
