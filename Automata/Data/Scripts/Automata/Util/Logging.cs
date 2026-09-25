using System;
using System.Text;
using Sandbox.ModAPI;
using VRage.Utils;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;

using Automata.Config;

namespace Automata.Util.Logging
{
    /// <summary>
    /// Simple static logging class that obeys server configuration
    /// </summary>
    public static class Log
    {
        private static LoggingEngine _logger;
        private static readonly object _lock = new object();
        public static readonly LogLevel LogLevel = ServerConfig.Instance.Logging.LogLevel;
        public static readonly bool logPathfinding = ServerConfig.Instance.Logging.LogPathfinding;
        public static readonly bool logDroneNetwork = ServerConfig.Instance.Logging.LogDroneNetwork;
        public static readonly bool logDroneOrders = ServerConfig.Instance.Logging.LogDroneOrders;

        /// <summary>
        /// Initialize the logging system
        /// </summary>
        public static void Initialize(string modName, int workshopId, string logFileName, Type typeOfMod)
        {
            if (_logger == null)
            {
                lock (_lock)
                {
                    if (_logger == null)
                    {
                        _logger = new LoggingEngine(modName, workshopId, logFileName, typeOfMod);
                    }
                }
            }
        }

        /// <summary>
        /// Check if logging is initialized
        /// </summary>
        public static bool IsInitialized => _logger != null;

        // Standard log levels
        public static void Verbose(string msg, params object[] args) => WriteIfEnabled(LogLevel.Verbose, msg, args);
        public static void Info(string msg, params object[] args) => WriteIfEnabled(LogLevel.Info, msg, args);
        public static void Debug(string msg, params object[] args) => WriteIfEnabled(LogLevel.Debug, msg, args);
        public static void Warning(string msg, params object[] args) => WriteIfEnabled(LogLevel.Warning, msg, args);
        public static void Error(string msg, params object[] args) => WriteIfEnabled(LogLevel.Error, msg, args);
        public static void Error(Exception ex) => WriteIfEnabled(LogLevel.Error, ex.ToString());

        // Specialized logging that checks server config flags
        public static void LogPathfinding(string msg, params object[] args)
        {
            if (logPathfinding && IsLevelEnabled(LogLevel.Debug))
            {
                _logger?.Write("[IAI.Pathfinding] " + string.Format(msg, args));
            }
        }

        public static void LogDroneNetwork(LogLevel level, string msg, params object[] args)
        {
            if (logDroneNetwork)
            {
                WriteIfEnabled(level, "[IAI.DroneNetwork] " + msg, args);
            }
        }

        public static void LogDroneOrders(string msg, params object[] args)
        {
            if (logDroneOrders && IsLevelEnabled(LogLevel.Info))
            {
                _logger?.Write("[IAI.DroneOrders] " + string.Format(msg, args));
            }
        }

        // Utility methods
        public static string BlockName(object block) => LoggingEngine.BlockName(block);
        public static void ShowOnHud(string text, int displayMs = 10000) => _logger?.ShowOnHud(text, displayMs);

        // Indentation for hierarchical logging
        public static void IncreaseIndent() => _logger?.IncreaseIndent();
        public static void DecreaseIndent() => _logger?.DecreaseIndent();
        public static void ResetIndent() => _logger?.ResetIndent();

        /// <summary>
        /// Force cleanup and close logger
        /// </summary>
        public static void Close()
        {
            lock (_lock)
            {
                _logger?.Close();
                _logger = null;
            }
        }

        private static void WriteIfEnabled(LogLevel level, string msg, params object[] args)
        {
            if (IsLevelEnabled(level))
            {
                var formattedMsg = args.Length > 0 ? string.Format(msg, args) : msg;

                if (level == LogLevel.Error)
                {
                    _logger?.WriteError(formattedMsg);
                }
                else
                {
                    _logger?.Write($"[IAI.{level.ToString().ToUpper()}] {formattedMsg}");
                }
            }
        }

        private static bool IsLevelEnabled(LogLevel level)
        {
            if (_logger == null) return false;

            // Check if the current log level allows this message
            return (level & ServerConfig.Instance.Logging.LogLevel) > 0;
        }
    }

    /// <summary>
    /// Internal logging engine - not exposed publicly
    /// </summary>
    internal class LoggingEngine
    {
        private string _modName;
        private int _workshopId;
        private string _logFilename;
        private Type _typeOfMod;

        private System.IO.TextWriter _writer = null;
        private IMyHudNotification _notify = null;
        private int _indent = 0;
        private StringBuilder _cache = new StringBuilder();

        [Flags]
        public enum BlockNameOptions
        {
            None = 0x0000,
            IncludeTypename = 0x0001
        }

        public LoggingEngine(string modName, int workshopId, string logFileName, Type typeOfMod)
        {
            MyLog.Default.WriteLineAndConsole(modName + " Create Log instance Utils=" + (MyAPIGateway.Utilities != null).ToString());
            _modName = modName;
            _workshopId = workshopId;
            _logFilename = logFileName;
            _typeOfMod = typeOfMod;
        }

        public static string BlockName(object block)
        {
            return BlockName(block, BlockNameOptions.IncludeTypename);
        }

        public static string BlockName(object block, BlockNameOptions options)
        {
            var inventory = block as IMyInventory;
            if (inventory != null)
            {
                block = inventory.Owner;
            }

            var slimBlock = block as IMySlimBlock;
            if (slimBlock != null)
            {
                if (slimBlock.FatBlock != null) block = slimBlock.FatBlock;
                else
                {
                    return string.Format("{0}.{1}", slimBlock.CubeGrid != null ? slimBlock.CubeGrid.DisplayName : "Unknown Grid", slimBlock.BlockDefinition.DisplayNameText);
                }
            }

            var terminalBlock = block as IMyTerminalBlock;
            if (terminalBlock != null)
            {
                if ((options & BlockNameOptions.IncludeTypename) != 0) return string.Format("{0}.{1} [{2}]", terminalBlock.CubeGrid != null ? terminalBlock.CubeGrid.DisplayName : "Unknown Grid", terminalBlock.CustomName, terminalBlock.BlockDefinition.TypeIdString);
                return string.Format("{0}.{1}", terminalBlock.CubeGrid != null ? terminalBlock.CubeGrid.DisplayName : "Unknown Grid", terminalBlock.CustomName);
            }

            var cubeBlock = block as IMyCubeBlock;
            if (cubeBlock != null)
            {
                return string.Format("{0} [{1}/{2}]", cubeBlock.CubeGrid != null ? cubeBlock.CubeGrid.DisplayName : "Unknown Grid", cubeBlock.BlockDefinition.TypeIdString, cubeBlock.BlockDefinition.SubtypeName);
            }

            var entity = block as IMyEntity;
            if (entity != null)
            {
                if ((options & BlockNameOptions.IncludeTypename) != 0) return string.Format("{0} ({1}) [{2}]", string.IsNullOrEmpty(entity.DisplayName) ? entity.GetFriendlyName() : entity.DisplayName, entity.EntityId, entity.GetType().Name);
                return string.Format("{0} ({1})", entity.DisplayName, entity.EntityId);
            }

            return block != null ? block.ToString() : "NULL";
        }

        public void IncreaseIndent()
        {
            _indent++;
        }

        public void DecreaseIndent()
        {
            if (_indent > 0) _indent--;
        }

        public void ResetIndent()
        {
            _indent = 0;
        }

        public void WriteError(string msg)
        {
            Write("ERROR: " + msg);

            try
            {
                MyLog.Default.WriteLineAndConsole(_modName + " error: " + msg);

                string text = _modName + " error - open %AppData%/SpaceEngineers/Storage/" + _logFilename + " for details";

                // Always show errors on HUD regardless of config
                ShowOnHud(text);
            }
            catch (Exception e)
            {
                Write(string.Format("ERROR: Could not send notification to local client: " + e.ToString()));
            }
        }

        public void Write(string msg)
        {
            try
            {
                lock (_cache)
                {
                    _cache.Append(DateTime.Now.ToString("u") + ":");

                    for (int i = 0; i < _indent; i++)
                    {
                        _cache.Append("   ");
                    }

                    _cache.Append(msg).AppendLine();

                    if (_writer == null && MyAPIGateway.Utilities != null)
                    {
                        _writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(_logFilename, _typeOfMod);
                    }

                    if (_writer != null)
                    {
                        _writer.Write(_cache);
                        _writer.Flush();
                        _cache.Clear();
                    }
                }
            }
            catch (Exception e)
            {
                MyLog.Default.WriteLineAndConsole(_modName + " Error while logging message='" + msg + "'\nLogger error: " + e.Message + "\n" + e.StackTrace);
            }
        }

        public void ShowOnHud(string text, int displayMs = 10000)
        {
            if (_notify == null)
            {
                _notify = MyAPIGateway.Utilities.CreateNotification(text, displayMs, MyFontEnum.Red);
            }
            else
            {
                _notify.Text = text;
                _notify.ResetAliveTime();
            }

            _notify.Show();
        }

        public void Close()
        {
            lock (_cache)
            {
                if (_writer != null)
                {
                    _writer.Flush();
                    _writer.Close();
                    _writer.Dispose();
                    _writer = null;
                }

                _indent = 0;
                _cache.Clear();
            }
        }
    }
}