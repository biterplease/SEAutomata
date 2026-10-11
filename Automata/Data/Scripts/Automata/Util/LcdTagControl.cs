using System;
using System.Text;

using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Utils;

namespace Automata.Util
{
    /// <summary>The "LCD tag" textbox of the Automata programmable-block computers (see <see cref="BlockLcd"/>).</summary>
    public static class LcdTagControl
    {
        #region UI strings
        private static readonly MyStringId StrLcdTag = MyStringId.GetOrCompute("LCD tag");
        private static readonly MyStringId StrLcdTagTip = MyStringId.GetOrCompute("LCD panels on this construct with this text in their name, whose content is set to the Automata Log app, show this block's status (empty: {0}).");
        #endregion

        public static void Add<T>(string id, Func<IMyTerminalBlock, T> logic, Func<T, string> get, Action<T, string> set, string defaultTag) where T : class
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, IMyProgrammableBlock>(id);
            c.Title = StrLcdTag;
            c.Tooltip = Loc.Id(StrLcdTagTip, defaultTag);
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
        #region UI strings
        public static readonly MyStringId StrUseBroadcastController = MyStringId.GetOrCompute("Use broadcast controller");
        private static readonly MyStringId StrUseBroadcastControllerTip = MyStringId.GetOrCompute("Sends a chat line through a Broadcast Controller on this construct (its Broadcast target decides who reads it: owner, faction or everyone; its name is the sender). Nothing is sent without a working one.\nSent: {0}");
        #endregion

        /// <summary>The checkbox tooltip; 'sends': what the block sends (localized).</summary>
        public static MyStringId Tooltip(MyStringId sends)
        {
            return Loc.Id(StrUseBroadcastControllerTip, Loc.T(sends));
        }

        /// <summary>'sends': what the block sends (localized), for the tooltip.</summary>
        public static void Add<T>(string id, Func<IMyTerminalBlock, T> logic, Func<T, bool> get, Action<T, bool> set, MyStringId sends) where T : class
        {
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlCheckbox, IMyProgrammableBlock>(id);
            c.Title = StrUseBroadcastController;
            c.Tooltip = Tooltip(sends);
            c.SupportsMultipleBlocks = true;
            c.Visible = b => logic(b) != null;
            c.Getter = b => { var l = logic(b); return l != null && get(l); };
            c.Setter = (b, v) => { var l = logic(b); if (l != null) set(l, v); };
            MyAPIGateway.TerminalControls.AddControl<IMyProgrammableBlock>(c);
        }
    }
}
