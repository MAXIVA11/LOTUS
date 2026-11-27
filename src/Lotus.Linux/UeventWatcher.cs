using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Lotus.Linux
{
    /// <summary>
    /// Hears about USB devices from the kernel itself. The kernel announces every
    /// device that appears or goes away on a netlink socket, and this listens on
    /// the group only the kernel can send to. If the socket cannot be opened, the
    /// list of devices is checked a few times a second instead.
    /// </summary>
    public sealed class UeventWatcher : IDisposable
    {
        private const int AF_NETLINK = 16;
        private const int SOCK_DGRAM = 2;
        private const int SOCK_CLOEXEC = 0x80000;
        private const int NETLINK_KOBJECT_UEVENT = 15;
        private const int SOL_SOCKET = 1;
        private const int SO_RCVTIMEO = 20;
        private const int SO_RCVBUF = 8;
        private const uint KernelGroup = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct sockaddr_nl
        {
            public ushort nl_family;
            public ushort nl_pad;
            public uint nl_pid;
            public uint nl_groups;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TimeValue
        {
            public long tv_sec;
            public long tv_usec;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int socket(int domain, int type, int protocol);

        [DllImport("libc", SetLastError = true)]
        private static extern int bind(int fd, ref sockaddr_nl address, int length);

        [DllImport("libc", SetLastError = true)]
        private static extern IntPtr recv(int fd, byte[] buffer, UIntPtr length, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int setsockopt(int fd, int level, int name, ref TimeValue value, int length);

        [DllImport("libc", SetLastError = true)]
        private static extern int setsockopt(int fd, int level, int name, ref int value, int length);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);

        private readonly Action<bool> onChange;
        private readonly string sysfsRoot;
        private Thread thread;
        private volatile bool stopping;
        private int fd = -1;

        /// <summary>True when the kernel's own announcements are used, false when the list is polled.</summary>
        public bool UsesNetlink { get; private set; }

        /// <param name="onChange">Called with true when a device appeared and false when one went away.</param>
        public UeventWatcher(Action<bool> onChange, string sysfsRoot = LinuxDeviceBackend.DefaultRoot)
        {
            this.onChange = onChange ?? throw new ArgumentNullException(nameof(onChange));
            this.sysfsRoot = sysfsRoot;
        }

        public void Start()
        {
            if (thread != null) return;
            // A made-up device tree has no kernel behind it to announce anything.
            UsesNetlink = sysfsRoot == LinuxDeviceBackend.DefaultRoot && TryOpen();
            thread = new Thread(UsesNetlink ? (ThreadStart)Listen : Poll) { IsBackground = true, Name = "LOTUS devices" };
            thread.Start();
        }

        private bool TryOpen()
        {
            try
            {
                fd = socket(AF_NETLINK, SOCK_DGRAM | SOCK_CLOEXEC, NETLINK_KOBJECT_UEVENT);
                if (fd < 0) return false;
                int size = 1024 * 1024;
                setsockopt(fd, SOL_SOCKET, SO_RCVBUF, ref size, sizeof(int));
                // Wake up once a second to see whether it is time to stop.
                var wait = new TimeValue { tv_sec = 1, tv_usec = 0 };
                setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, ref wait, Marshal.SizeOf(typeof(TimeValue)));
                var address = new sockaddr_nl { nl_family = AF_NETLINK, nl_pid = 0, nl_groups = KernelGroup };
                if (bind(fd, ref address, Marshal.SizeOf(typeof(sockaddr_nl))) != 0)
                {
                    close(fd);
                    fd = -1;
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                fd = -1;
                return false;
            }
        }

        private void Listen()
        {
            var buffer = new byte[16 * 1024];
            while (!stopping)
            {
                long n = recv(fd, buffer, (UIntPtr)buffer.Length, 0).ToInt64();
                if (n <= 0) continue; // the one-second timeout, or a message that was too long
                bool? arrived = Parse(buffer, (int)n);
                if (arrived.HasValue) Notify(arrived.Value);
            }
        }

        /// <summary>
        /// A message is a line like "add@/devices/..." followed by KEY=value pairs,
        /// all separated by zero bytes. Returns null for anything that is not a
        /// whole USB device coming or going.
        /// </summary>
        public static bool? Parse(byte[] buffer, int length)
        {
            string text = Encoding.UTF8.GetString(buffer, 0, length);
            string action = null, subsystem = null, type = null;
            foreach (string part in text.Split('\0'))
            {
                if (part.StartsWith("ACTION=", StringComparison.Ordinal)) action = part.Substring(7);
                else if (part.StartsWith("SUBSYSTEM=", StringComparison.Ordinal)) subsystem = part.Substring(10);
                else if (part.StartsWith("DEVTYPE=", StringComparison.Ordinal)) type = part.Substring(8);
            }
            if (subsystem != "usb" || type != "usb_device") return null;
            if (action == "add" || action == "change" || action == "bind") return true;
            if (action == "remove") return false;
            return null;
        }

        private void Poll()
        {
            var known = new HashSet<string>(Names());
            while (!stopping)
            {
                Thread.Sleep(250);
                var now = new HashSet<string>(Names());
                if (now.SetEquals(known)) continue;
                bool arrived = !now.IsSubsetOf(known);
                known = now;
                Notify(arrived);
            }
        }

        private IEnumerable<string> Names()
        {
            try
            {
                var names = new List<string>();
                if (!Directory.Exists(sysfsRoot)) return names;
                foreach (string path in Directory.GetDirectories(sysfsRoot))
                {
                    string name = Path.GetFileName(path);
                    if (LinuxDeviceBackend.IsDeviceName(name)) names.Add(name);
                }
                return names;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        private void Notify(bool arrived)
        {
            try
            {
                onChange(arrived);
            }
            catch (Exception)
            {
                // A listener that fails must not end the watching.
            }
        }

        public void Dispose()
        {
            stopping = true;
            Thread t = thread;
            thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(3));
            if (fd >= 0)
            {
                close(fd);
                fd = -1;
            }
        }
    }
}
