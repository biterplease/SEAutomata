using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VRage;

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


                    }
                }
            }
        }
        private string GetTaskList()
        {

        }
        private string GetHelpText()
        {
            var text = string.Format(Texts.Cmd_HelpClient.String, IAISession.VERSION, CmdHelp1, CmdHelp2,
               CmdLogLevel, CmdLogLevel_All, CmdLogLevel_Default,
               CmdWriteTranslation, string.Join(",", Enum.GetNames(typeof(MyLanguagesEnum))), MyAPIGateway.Utilities.GamePaths.UserDataPath + Path.DirectorySeparatorChar + "Storage" + Path.DirectorySeparatorChar + MyAPIGateway.Utilities.GamePaths.ModScopeName);
            if (MyAPIGateway.Session.IsServer) text += string.Format(Texts.Cmd_HelpServer.String, CmdCwsf, CmdCpsf);
            return text;
        }
    }
}
