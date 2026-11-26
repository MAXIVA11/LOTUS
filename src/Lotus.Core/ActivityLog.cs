using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lotus.Core
{
    public static class EventTypes
    {
        public const string GuardStarted = "guard_started";
        public const string GuardStopped = "guard_stopped";
        public const string PolicyChanged = "policy_changed";
        public const string Connected = "connected";
        public const string Removed = "removed";
        public const string Blocked = "blocked";
        public const string Released = "released";
        public const string ReadOnlyOn = "readonly_on";
        public const string ReadOnlyOff = "readonly_off";
        public const string Warning = "warning";
        public const string Error = "error";
    }

    public sealed class LotusEvent
    {
        public DateTime TimeUtc = DateTime.UtcNow;
        public string Type = "";
        /// <summary>Instance ID of the device the event is about, or empty.</summary>
        public string DeviceId = "";
        public string DeviceName = "";
        public string Message = "";

        public string ToJsonLine()
        {
            var obj = new Dictionary<string, object>
            {
                ["t"] = Json.FormatTime(TimeUtc),
                ["type"] = Type ?? "",
                ["msg"] = Message ?? ""
            };
            if (!string.IsNullOrEmpty(DeviceId)) obj["id"] = DeviceId;
            if (!string.IsNullOrEmpty(DeviceName)) obj["name"] = DeviceName;
            return Json.Write(obj, false);
        }

        public static LotusEvent FromJsonLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || !Json.TryParse(line, out object value)) return null;
            Dictionary<string, object> obj = Json.AsObject(value);
            if (obj == null) return null;
            return new LotusEvent
            {
                TimeUtc = Json.ParseTime(Json.GetString(obj, "t", null), DateTime.MinValue),
                Type = Json.GetString(obj, "type", ""),
                DeviceId = Json.GetString(obj, "id", ""),
                DeviceName = Json.GetString(obj, "name", ""),
                Message = Json.GetString(obj, "msg", "")
            };
        }
    }

    /// <summary>
    /// The activity log: one JSON object per line. The service appends to it and
    /// the app reads it while the service keeps writing.
    /// </summary>
    public sealed class ActivityLog
    {
        private const long MaxBytes = 1024 * 1024;
        private readonly string path;
        private readonly string oldPath;
        private readonly object gate = new object();

        public ActivityLog(string path, string oldPath)
        {
            this.path = path;
            this.oldPath = oldPath;
        }

        public string FilePath => path;

        public void Write(LotusEvent e)
        {
            if (e == null) return;
            lock (gate)
            {
                try
                {
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    RotateIfLarge();
                    byte[] bytes = new UTF8Encoding(false).GetBytes(e.ToJsonLine() + "\n");
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                               FileShare.ReadWrite | FileShare.Delete))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Logging must never stop enforcement.
                }
            }
        }

        public void Write(string type, string message, UsbDevice device = null)
        {
            Write(new LotusEvent
            {
                TimeUtc = DateTime.UtcNow,
                Type = type,
                Message = message,
                DeviceId = device?.InstanceId ?? "",
                DeviceName = device?.Name ?? ""
            });
        }

        private void RotateIfLarge()
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(path, oldPath);
        }

        /// <summary>The newest events, newest first.</summary>
        public List<LotusEvent> ReadRecent(int max)
        {
            var events = new List<LotusEvent>();
            foreach (string file in new[] { oldPath, path })
            {
                foreach (string line in ReadLines(file))
                {
                    LotusEvent e = LotusEvent.FromJsonLine(line);
                    if (e != null) events.Add(e);
                }
            }
            if (events.Count > max) events.RemoveRange(0, events.Count - max);
            events.Reverse();
            return events;
        }

        private static List<string> ReadLines(string file)
        {
            var lines = new List<string>();
            try
            {
                if (!File.Exists(file)) return lines;
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return lines;
        }
    }

    /// <summary>
    /// Follows the activity log from the point where it was opened and hands back
    /// only the events written since. The tray notifier uses it.
    /// </summary>
    public sealed class ActivityTail
    {
        private readonly string path;
        private long position;

        public ActivityTail(string path)
        {
            this.path = path;
            try
            {
                position = File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (IOException)
            {
                position = 0;
            }
        }

        public List<LotusEvent> ReadNew()
        {
            var events = new List<LotusEvent>();
            try
            {
                if (!File.Exists(path)) { position = 0; return events; }
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    // The log was rotated or cleared, so start again from the top.
                    if (stream.Length < position) position = 0;
                    if (stream.Length == position) return events;
                    stream.Seek(position, SeekOrigin.Begin);
                    var buffer = new byte[stream.Length - position];
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return events;
                    // Only consume whole lines. A line still being written waits for next time.
                    int lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
                    if (lastNewline < 0) return events;
                    position += lastNewline + 1;
                    string text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
                    foreach (string line in text.Split('\n'))
                    {
                        LotusEvent e = LotusEvent.FromJsonLine(line);
                        if (e != null) events.Add(e);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
            return events;
        }
    }
}
