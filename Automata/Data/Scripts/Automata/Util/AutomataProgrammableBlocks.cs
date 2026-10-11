using System.Collections.Generic;

using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;

using Automata.Util.Logging;

namespace Automata.Util
{
    /// <summary>
    /// The Automata blocks built on the programmable block (orchestrator, logistics / construction computer): the
    /// programmable block's own controls (script editor, argument, run, recompile) and run actions are hidden on
    /// them - On / Off, the generic terminal fields and the LCD controls stay. Vanilla programmable blocks keep all.
    /// </summary>
    public static class AutomataProgrammableBlocks
    {
        private static bool done;

        private static readonly HashSet<string> controlsToHide = new HashSet<string>
        {
            "Edit",
            "ConsoleCommand",
            "TerminalRun",
            "Recompile",
        };
        private static readonly HashSet<string> actionsToHide = new HashSet<string>
        {
            "Run",
            "RunWithDefaultArgument",
        };

        /// <summary>An Automata block (subtype "Automata...").</summary>
        public static bool IsAutomata(IMyTerminalBlock b)
        {
            return b != null && b.BlockDefinition.SubtypeName.StartsWith("Automata", System.StringComparison.Ordinal);
        }

        private static bool NotAutomata(IMyTerminalBlock b)
        {
            return !IsAutomata(b);
        }

        public static void HideVanillaOnce()
        {
            if (done) return;
            done = true;
            List<IMyTerminalControl> controls;
            MyAPIGateway.TerminalControls.GetControls<IMyProgrammableBlock>(out controls);
            int hidden = 0;
            foreach (var c in controls)
            {
                if (!controlsToHide.Contains(c.Id)) continue;
                c.Visible = TerminalChainedDelegate.Create(c.Visible, NotAutomata);
                hidden++;
            }
            List<IMyTerminalAction> actions;
            MyAPIGateway.TerminalControls.GetActions<IMyProgrammableBlock>(out actions);
            foreach (var a in actions)
                if (actionsToHide.Contains(a.Id)) a.Enabled = TerminalChainedDelegate.Create(a.Enabled, NotAutomata);
            Log.Info("Programmable block controls hidden on Automata blocks: {0}", hidden);
        }
    }
}
