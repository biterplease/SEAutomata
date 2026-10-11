using System;
using System.Collections.Generic;
using System.Text;

using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.ModAPI;
using VRage.Utils;

using Automata.Inventory;

namespace Automata.Util
{
    /// <summary>An Automata block with a player-set nickname (the name its logs, and other blocks' logs, show).</summary>
    public interface INicknamed
    {
        /// <summary>The nickname as set (may be empty).</summary>
        string Nickname { get; }
    }

    /// <summary>
    /// The human-readable name of Automata blocks in logs: the block's "Nickname" setting, else its terminal name,
    /// cut to 16 characters ("..." at the end when cut).
    /// </summary>
    public static class Nicknames
    {
        public const int MAX = 16;

        public static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            return s.Length <= MAX ? s : s.Substring(0, MAX - 3) + "...";
        }

        /// <summary>The nickname of the block with this entity id (server side: every block is there).</summary>
        public static string Of(long entityId)
        {
            if (entityId == 0) return "-";
            IMyEntity e;
            if (MyAPIGateway.Entities == null || !MyAPIGateway.Entities.TryGetEntityById(entityId, out e) || e == null) return "#" + entityId;
            return Of(e);
        }

        public static string Of(IMyEntity e)
        {
            if (e == null) return "-";
            string nick = Raw(e);
            if (!string.IsNullOrWhiteSpace(nick)) return Short(nick);
            var tb = e as IMyTerminalBlock;
            if (tb != null) return Short(tb.CustomName);
            return Short(e.DisplayName ?? ("#" + e.EntityId));
        }

        private static string Raw(IMyEntity e)
        {
            var gl = e.GameLogic;
            if (gl == null) return null;
            var n = gl as INicknamed;
            if (n != null) return n.Nickname;
            // Composite logic (several components on one block): the known Automata ones
            INicknamed found = gl.GetAs<Drone.DroneControllerBlock>();
            if (found == null) found = gl.GetAs<Orchestrator.OrchestratorBlock>();
            if (found == null) found = gl.GetAs<Logistics.LogisticsComputerBlock>();
            if (found == null) found = gl.GetAs<Mining.MiningSurveyorBlock>();
            if (found == null) found = gl.GetAs<Construction.ConstructionComputerBlock>();
            return found != null ? found.Nickname : null;
        }

        /// <summary>The player-facing name of an item key ("SteelPlate", "Ore/Iron"): its display name, else the subtype.</summary>
        public static string Item(MyStringHash key)
        {
            ComponentData data;
            if (AutomataSession.componentLookup.TryGetValue(key, out data) && !string.IsNullOrEmpty(data.DisplayName)) return data.DisplayName;
            string s = key.String ?? "";
            int slash = s.IndexOf('/');
            return slash >= 0 ? s.Substring(slash + 1) : s;
        }

        /// <summary>A log entry's payload: 'label' on its own line (2 spaces in), then one line per item (4 spaces in).</summary>
        public static void AppendPayload(StringBuilder sb, string label, DiscreteInventory items)
        {
            if (items == null || items.IsEmpty()) return;
            sb.Append('\n').Append("  ").Append(label);
            var list = items.GetAllItems();
            for (int i = 0; i < list.Count; i++)
                if (list[i].Value > 0) sb.Append('\n').Append("    ").Append(Item(list[i].Key)).Append(": ").Append(list[i].Value);
        }
    }

    /// <summary>
    /// The "Nickname" textbox of every Automata block, moved right under the game's own "Name" field
    /// (<see cref="IMyTerminalControls.CustomControlGetter"/>).
    /// </summary>
    public static class NicknameControl
    {
        #region UI strings
        private static readonly MyStringId StrNickname = MyStringId.GetOrCompute("Nickname");
        private static readonly MyStringId StrNicknameTip = MyStringId.GetOrCompute("Nickname that will show in LCD logs");
        #endregion

        public const string ID_SUFFIX = "_Nickname";
        private static bool registered;

        public static void Add<TBlock, T>(string id, Func<IMyTerminalBlock, T> logic, Func<T, string> get, Action<T, string> set)
            where T : class
        {
            Register();
            var c = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlTextbox, TBlock>(id + ID_SUFFIX);
            c.Title = StrNickname;
            c.Tooltip = StrNicknameTip;
            c.SupportsMultipleBlocks = false;
            c.Visible = b => logic(b) != null;
            c.Getter = b => { var l = logic(b); return new StringBuilder(l != null ? get(l) ?? "" : ""); };
            c.Setter = (b, v) =>
            {
                var l = logic(b);
                if (l == null) return;
                set(l, v != null ? v.ToString().Trim() : "");
            };
            MyAPIGateway.TerminalControls.AddControl<TBlock>(c);
        }

        /// <summary>Session load (and on first use): the nickname moves under "Name".</summary>
        public static void Register()
        {
            if (registered) return;
            registered = true;
            MyAPIGateway.TerminalControls.CustomControlGetter += PlaceUnderName;
        }

        public static void Unregister()
        {
            if (!registered) return;
            registered = false;
            MyAPIGateway.TerminalControls.CustomControlGetter -= PlaceUnderName;
        }

        // Automata blocks: the nickname right after the game's "Name" textbox
        private static void PlaceUnderName(IMyTerminalBlock block, List<IMyTerminalControl> controls)
        {
            if (!AutomataProgrammableBlocks.IsAutomata(block)) return;
            int nick = -1, name = -1;
            for (int i = 0; i < controls.Count; i++)
            {
                string id = controls[i].Id;
                if (id == null) continue;
                if (id == "Name") name = i;
                else if (nick < 0 && id.EndsWith(ID_SUFFIX, StringComparison.Ordinal) && controls[i].Visible(block)) nick = i;
            }
            if (nick < 0 || name < 0 || nick == name + 1) return;
            var c = controls[nick];
            controls.RemoveAt(nick);
            if (nick < name) name--;
            controls.Insert(name + 1, c);
        }
    }
}
