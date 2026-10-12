using System;
using System.Text;
using Automata.Util.Strings;

using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Utils;

namespace Automata.Util
{
    /// <summary>The "LCD tag" textbox of the Automata programmable-block computers (see <see cref="BlockLcd"/>).</summary>
    public static class LcdTagControl
    {
        public static void Add<T>(string id, Func<IMyTerminalBlock, T> logic, Func<T, string> get, Action<T, string> set, string defaultTag) where T : class
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyProgrammableBlock>(id);
            c.Title = MyStrings.LCDScreenTag;
            c.Tooltip = Loc.Id(MyStrings.LCDScreenTagTip, defaultTag);
            c.SupportsMultipleBlocks = true;
            c.Visible = b => logic(b) != null;
            c.Getter = b => { var l = logic(b); return new StringBuilder(l != null ? get(l) : ""); };
            c.Setter = (b, v) =>
            {
                var l = logic(b);
                if (l == null) return;
                string s = v != null ? v.ToString().Trim() : "";
                set(l, s.Length > 0 ? s : defaultTag);
            };
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
        }
    }

    /// <summary>
    /// The "Use broadcast controller" checkbox of the Automata programmable-block computers, next to the LCD tag: a few
    /// events worth a chat line go out through a Broadcast Controller on the block's construct
    /// (<see cref="BroadcastControllerSender"/>). 'sends': what this block sends, for the tooltip.
    /// </summary>
    public static class BroadcastControllerControl
    {
        /// <summary>'sends': what the block sends (localized), for the tooltip.</summary>
        public static void Add<T>(string id, Func<IMyTerminalBlock, T> logic, Func<T, bool> get, Action<T, bool> set) where T : class
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(id);
            c.Title = MyStrings.UseBroadcastController;
            c.Tooltip = MyStrings.BLANK;
            c.SupportsMultipleBlocks = true;
            c.Visible = b => logic(b) != null;
            c.Getter = b => { var l = logic(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = logic(b); if (l != null) set(l, v); };
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
        }
    }
}
