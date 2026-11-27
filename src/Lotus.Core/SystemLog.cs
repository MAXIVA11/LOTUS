using System;
using System.Text;

namespace Lotus.Core
{
    /// <summary>
    /// The operating system's own log: the Windows event log, or the journal on
    /// Linux. The service copies every line of its activity log there, because
    /// that is where the tools that collect logs from many machines look, and
    /// because the system decides how long it is kept.
    /// </summary>
    public interface ISystemLog
    {
        void Write(LotusEvent e);
    }

    /// <summary>How serious an event is, in the three steps both systems know.</summary>
    public enum EventLevel
    {
        Information,
        Warning,
        Error
    }

    /// <summary>
    /// The number each kind of event carries in the system log. These numbers are
    /// part of what LOTUS promises: a rule in a log collector that looks for 2100
    /// keeps working after an update. A new kind of event gets a new number, and
    /// a number is never given a second meaning.
    /// </summary>
    public static class EventIds
    {
        public const int GuardStarted = 1000;
        public const int GuardStopped = 1001;
        public const int PolicyChanged = 1100;
        public const int TrustEnded = 1101;
        public const int Connected = 2000;
        public const int Removed = 2001;
        public const int Blocked = 2100;
        public const int Released = 2101;
        public const int InputLetIn = 2102;
        public const int ReadOnlyOn = 2200;
        public const int ReadOnlyOff = 2201;
        public const int Warning = 3000;
        public const int Error = 3001;
        public const int Tamper = 4000;
        public const int Security = 4100;
        /// <summary>A kind of event this table does not know. It should not happen.</summary>
        public const int Other = 9000;
        /// <summary>The longest message or ID that goes into the system's log.</summary>
        public const int MaxText = 4000;

        public static int For(string type)
        {
            switch (type)
            {
                case EventTypes.GuardStarted: return GuardStarted;
                case EventTypes.GuardStopped: return GuardStopped;
                case EventTypes.PolicyChanged: return PolicyChanged;
                case EventTypes.TrustEnded: return TrustEnded;
                case EventTypes.Connected: return Connected;
                case EventTypes.Removed: return Removed;
                case EventTypes.Blocked: return Blocked;
                case EventTypes.Released: return Released;
                case EventTypes.InputLetIn: return InputLetIn;
                case EventTypes.ReadOnlyOn: return ReadOnlyOn;
                case EventTypes.ReadOnlyOff: return ReadOnlyOff;
                case EventTypes.Warning: return Warning;
                case EventTypes.Error: return Error;
                case EventTypes.Tamper: return Tamper;
                case EventTypes.Security: return Security;
                default: return Other;
            }
        }

        public static EventLevel LevelFor(string type)
        {
            switch (type)
            {
                case EventTypes.Blocked:
                case EventTypes.InputLetIn:
                case EventTypes.Warning:
                case EventTypes.Security:
                    return EventLevel.Warning;
                case EventTypes.Error:
                case EventTypes.Tamper:
                    return EventLevel.Error;
                default:
                    return EventLevel.Information;
            }
        }

        /// <summary>The event as a few lines of text, the way the Windows event log shows it.</summary>
        public static string Describe(LotusEvent e)
        {
            var text = new StringBuilder(TextSafety.Clean(e.Message ?? "", MaxText));
            if (!string.IsNullOrEmpty(e.DeviceName)) text.Append("\r\nDevice: ").Append(TextSafety.Clean(e.DeviceName));
            if (!string.IsNullOrEmpty(e.DeviceId)) text.Append("\r\nID: ").Append(TextSafety.Clean(e.DeviceId, MaxText));
            text.Append("\r\nEvent: ").Append(TextSafety.Clean(e.Type ?? ""));
            return text.ToString();
        }
    }

#if NETFRAMEWORK
    /// <summary>
    /// Writes to the Application log of Windows under the source "LotusGuard".
    /// The source is registered on first use, which the service may do because it
    /// runs as the system account. Removal takes the registration away again.
    /// </summary>
    public sealed class WindowsEventLog : ISystemLog
    {
        public const string Source = "LotusGuard";
        private const string LogName = "Application";
        private bool registered;
        private bool gaveUp;

        public void Write(LotusEvent e)
        {
            if (e == null || gaveUp) return;
            try
            {
                if (!registered)
                {
                    if (!System.Diagnostics.EventLog.SourceExists(Source))
                    {
                        System.Diagnostics.EventLog.CreateEventSource(
                            new System.Diagnostics.EventSourceCreationData(Source, LogName));
                    }
                    registered = true;
                }
                System.Diagnostics.EventLogEntryType type;
                switch (EventIds.LevelFor(e.Type))
                {
                    case EventLevel.Error: type = System.Diagnostics.EventLogEntryType.Error; break;
                    case EventLevel.Warning: type = System.Diagnostics.EventLogEntryType.Warning; break;
                    default: type = System.Diagnostics.EventLogEntryType.Information; break;
                }
                System.Diagnostics.EventLog.WriteEntry(Source, EventIds.Describe(e), type, EventIds.For(e.Type));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is InvalidOperationException ||
                                       ex is System.ComponentModel.Win32Exception || ex is ArgumentException ||
                                       ex is UnauthorizedAccessException || ex is System.IO.IOException)
            {
                // Without the right to register the source there is no point in
                // trying again for every line. A full log, on the other hand, is
                // something the next line may get past.
                if (!registered) gaveUp = true;
            }
        }
    }
#endif
}
