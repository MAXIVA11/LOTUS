using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Lotus.Core;

namespace Lotus.Linux
{
    /// <summary>
    /// Writes to the systemd journal over its own socket, so that every line
    /// carries fields a log collector can filter on:
    ///
    ///     journalctl -t lotus LOTUS_EVENT=blocked
    ///
    /// The journal adds who sent the line (the process, the user, the unit), and
    /// those fields cannot be forged by the sender.
    /// </summary>
    public sealed class JournalLog : ISystemLog, IDisposable
    {
        public const string DefaultSocket = "/run/systemd/journal/socket";
        private readonly string socketPath;
        private Socket socket;

        public JournalLog(string socketPath = DefaultSocket)
        {
            this.socketPath = socketPath;
        }

        /// <summary>True when this system has a journal to write to.</summary>
        public static bool Available(string socketPath = DefaultSocket)
        {
            try
            {
                return File.Exists(socketPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void Write(LotusEvent e)
        {
            if (e == null) return;
            byte[] datagram = Format(e);
            try
            {
                if (socket == null) socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                socket.SendTo(datagram, new UnixDomainSocketEndPoint(socketPath));
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is ObjectDisposedException ||
                                       ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                // The journal was restarted or is not there. The next line tries with a new socket.
                try { socket?.Dispose(); } catch (Exception) { }
                socket = null;
            }
        }

        /// <summary>One journal entry in the journal's own format: a field, an equals sign, the value, a line end.</summary>
        public static byte[] Format(LotusEvent e)
        {
            int priority;
            switch (EventIds.LevelFor(e.Type))
            {
                case EventLevel.Error: priority = 3; break;
                case EventLevel.Warning: priority = 4; break;
                default: priority = 6; break;
            }
            var text = new StringBuilder();
            Field(text, "MESSAGE", string.IsNullOrEmpty(e.DeviceName) ? e.Message : e.Message + " [" + e.DeviceName + "]");
            Field(text, "PRIORITY", priority.ToString());
            Field(text, "SYSLOG_IDENTIFIER", "lotus");
            Field(text, "LOTUS_EVENT", e.Type);
            Field(text, "LOTUS_EVENT_ID", EventIds.For(e.Type).ToString());
            if (!string.IsNullOrEmpty(e.DeviceId)) Field(text, "LOTUS_DEVICE_ID", e.DeviceId);
            if (!string.IsNullOrEmpty(e.DeviceName)) Field(text, "LOTUS_DEVICE_NAME", e.DeviceName);
            return new UTF8Encoding(false).GetBytes(text.ToString());
        }

        private static void Field(StringBuilder text, string name, string value)
        {
            // A line end inside a value would start a new field, so it never gets in.
            string clean = TextSafety.Clean(value ?? "", EventIds.MaxText).Replace('\n', ' ').Replace('\r', ' ');
            text.Append(name).Append('=').Append(clean).Append('\n');
        }

        public void Dispose()
        {
            try { socket?.Dispose(); } catch (Exception) { }
            socket = null;
        }
    }
}
