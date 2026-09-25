using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;

namespace Automata.Util
{
    /// <summary>
    /// Manages terminal text output for a block, similar to PB Echo behavior.
    /// Maintains a fixed-size FIFO message buffer.
    /// </summary>
    public sealed class TerminalDisplayManager : IDisposable
    {
        private readonly IMyTerminalBlock _terminalBlock;
        private readonly Queue<string> _lines;
        private readonly int _displayLines;
        private bool _isDisposed;

        public TerminalDisplayManager(IMyTerminalBlock terminalBlock, int displayLines)
        {
            if (terminalBlock == null)
            {
                throw new ArgumentNullException(nameof(terminalBlock));
            }

            _terminalBlock = terminalBlock;
            _displayLines = displayLines > 0 ? displayLines : 1;
            _lines = new Queue<string>(_displayLines);

            _terminalBlock.AppendingCustomInfo += AppendCustomInfo;
            _terminalBlock.RefreshCustomInfo();
        }

        /// <summary>
        /// Appends a message to terminal output and trims oldest lines when full.
        /// </summary>
        public void Echo(string message, params object[] args)
        {
            if (_isDisposed)
            {
                return;
            }

            string text = args.Length > 0 ? string.Format(message, args) : message;
            _lines.Enqueue(text);

            while (_lines.Count > _displayLines)
            {
                _lines.Dequeue();
            }

            _terminalBlock.RefreshCustomInfo();
        }

        /// <summary>
        /// Clears all buffered lines and updates the terminal display.
        /// </summary>
        public void Clear()
        {
            if (_isDisposed)
            {
                return;
            }

            _lines.Clear();
            _terminalBlock.RefreshCustomInfo();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _terminalBlock.AppendingCustomInfo -= AppendCustomInfo;
            _isDisposed = true;
        }

        private void AppendCustomInfo(IMyTerminalBlock block, StringBuilder builder)
        {
            if (_isDisposed)
            {
                return;
            }

            foreach (string line in _lines)
            {
                builder.AppendLine(line);
            }
        }
    }
}