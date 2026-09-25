using Sandbox.ModAPI;
using System;
using System.IO;

namespace ImprovedAI
{
    public class IAISessionCommandHandler
    {
        public IAISessionCommandHandler() { }

        private const string CMDROOT = "/iai";
        private const string HELP = "-h";
        private const string HELPLONG = "-help";
        private const string LIST_TASKS = "list-tasks";
        private const string TASK_CMD = "task";
        private const string TASK_WELD = "weld";
        private const string TASK_GRIND = "grind";

        public void MessageEntered(string msg)
        {
            if (string.IsNullOrEmpty(msg))
                return;
            var cmd = msg.ToLower();
            if (cmd.StartsWith(CMDROOT))
            {
                var args = cmd.Remove(0, CMDROOT.Length).Trim().Split(' ');
                if (args.Length > 0)
                {
                    switch (args[0].Trim())
                    {
                        case LIST_TASKS:
                            MyAPIGateway.Utilities.ShowMessage(IAISession.MOD_NAME, GetTaskList());
                            break;
                    }
                }
            }
        }
        private string GetTaskList()
        {
            return "Task list: (not yet implemented)";
        }

        private string GetHelpText()
        {
            return string.Format(
                "ImprovedAI {0} - Commands: {1} {2}",
                IAISession.VERSION, LIST_TASKS, TASK_CMD);
        }
    }
}
