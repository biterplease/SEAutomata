using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Utils;

using Automata.Pointer;
using Automata.Util;
using Automata.Util.Strings;

namespace Automata.Designator
{
    /// <summary>
    /// Target designator terminal: the programmable block's own controls are hidden (On / Off, the generic terminal fields stay);
    /// nickname, LCD tag and style, the camera, and the "Designator:" toolbar actions.
    /// </summary>
    public static class TargetDesignatorTerminalControls
    {

        const string IdPrefix = AutomataSession.MOD_NAME + "_Designator_";
        static bool Done;
        const string ICON_START = @"Textures\GUI\Icons\Actions\Start.dds";
        const string ICON_REVERSE = @"Textures\GUI\Icons\Actions\Start.dds";
        const string ICON_DECREASE = @"Textures\GUI\Icons\Actions\Start.dds";

        public static void DoOnce()
        {
            if (Done) return;
            Done = true;
            AutomataProgrammableBlocks.HideVanillaOnce();
            NicknameControl.Add<IMyProgrammableBlock, TargetDesignatorBlock>(IdPrefix + "Nick", GetBlock, l => l.Nickname, (l, v) => l.Terminal_Nickname = v);
            LcdTagControl.Add(IdPrefix + "LcdTag", GetBlock, l => l.Terminal_LcdTag, (l, v) => l.Terminal_LcdTag = v, "[Designator]");
            LcdControls.Add(IdPrefix, GetBlock, l => l.Terminal_LcdStyle, l => l.Terminal_LcdEdited(), false);
            var sep = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSeparator, IMyProgrammableBlock>("");
            sep.Visible = Visible;
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(sep);
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCombobox, IMyProgrammableBlock>(IdPrefix + "Camera");
            c.Title = MyStrings.Designator_DesignatorCamera2;
            c.Tooltip = MyStrings.Designator_DesignatorCamera2Tooltip;
            c.Visible = b => { var l = GetBlock(b); if (l != null) shown = l; return l != null; };
            c.SupportsMultipleBlocks = false;
            c.ComboBoxContent = list => { if (shown != null) shown.Terminal_CameraItems(list); };   // the content callback gets no block: the one last shown
            c.Getter = b => { var l = GetBlock(b); if (l != null) shown = l; return l != null ? l.Terminal_CameraShown : 0; };
            c.Setter = (b, v) => { var l = GetBlock(b); if (l != null) l.Terminal_Camera = v; };
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
            Action("Tag", "TAG", DesignatorOrder.Tag, ICON_START);
            Action("Spot", "SPOT", DesignatorOrder.Spot, ICON_START);
            Action("Area", "AREA", DesignatorOrder.Area, ICON_START);
            Action("Engage", "ENGAGE", DesignatorOrder.Engage, ICON_START);
            Action("Escort", "ESCORT", DesignatorOrder.Escort, ICON_START);
            Action("Recall", "RECALL", DesignatorOrder.Recall, ICON_REVERSE);
            Action("Clear", "CLEAR", DesignatorOrder.Clear, ICON_DECREASE);
        }

        // Toolbar only ("Designator: TAG" ...): pressed by a player, done by the server with the designator camera
        static void Action(string id, string name, DesignatorOrder order, string icon)
        {
            var a = MyAPIGateway.TerminalControls.CreateAction<IMyProgrammableBlock>(IdPrefix + id);
            a.Name = new System.Text.StringBuilder("Designator: " + name);
            a.Icon = icon;
            a.ValidForGroups = false;
            a.Enabled = Visible;   // target designators only
            a.Action = b => { var l = GetBlock(b); if (l != null) l.Terminal_Designate(order); };
            a.Writer = (b, sb) => sb.Append(name);
            MyAPIGateway.TerminalControls.AddAction<IMyProgrammableBlock>(a);
        }

        static TargetDesignatorBlock shown;

        static TargetDesignatorBlock GetBlock(IMyTerminalBlock b)
        {
            return b != null && b.GameLogic != null ? b.GameLogic.GetAs<TargetDesignatorBlock>() : null;
        }

        static bool Visible(IMyTerminalBlock b) { return GetBlock(b) != null; }
    }
}
