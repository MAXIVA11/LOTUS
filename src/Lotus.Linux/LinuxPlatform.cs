using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Lotus.Core;

namespace Lotus.Core
{
    /// <summary>
    /// How the store key is kept on Linux. There is no data protection service
    /// to wrap it with, so what protects the key is the file itself: it belongs
    /// to root, only root may read it, and it sits in a folder only root may enter.
    /// </summary>
    public static class PlatformKeyWrap
    {
        public static IKeyWrap Create()
        {
            return new PlainKeyWrap();
        }
    }

    public sealed class PlainKeyWrap : IKeyWrap
    {
        private static readonly byte[] Mark = Encoding.ASCII.GetBytes("LOTUSKEY1\n");

        public byte[] Wrap(byte[] key)
        {
            var wrapped = new byte[Mark.Length + key.Length];
            Buffer.BlockCopy(Mark, 0, wrapped, 0, Mark.Length);
            Buffer.BlockCopy(key, 0, wrapped, Mark.Length, key.Length);
            return wrapped;
        }

        public byte[] Unwrap(byte[] wrapped)
        {
            if (wrapped == null || wrapped.Length <= Mark.Length) throw new FormatException("The key file is too short.");
            for (int i = 0; i < Mark.Length; i++)
            {
                if (wrapped[i] != Mark[i]) throw new FormatException("The key file is not a LOTUS key.");
            }
            var key = new byte[wrapped.Length - Mark.Length];
            Buffer.BlockCopy(wrapped, Mark.Length, key, 0, key.Length);
            return key;
        }
    }
}

namespace Lotus.Linux
{
    /// <summary>LOTUS on Linux: sysfs, the kernel's device announcements, a udev rule and a Unix socket.</summary>
    public sealed class LinuxPlatform : GuardPlatform
    {
        public const string DefaultStateDir = "/var/lib/lotus";

        private readonly string sysfsRoot;
        private readonly string readOnlyRulePath;
        private LinuxDeviceBackend backend;

        /// <param name="sysfsRoot">Where the kernel lists USB devices. Tests and the developer mode point this at a made-up tree.</param>
        /// <param name="readOnlyRulePath">Where the read-only rule goes. Left empty, it is the udev rules folder, and udev is told to read it.</param>
        public LinuxPlatform(string sysfsRoot = LinuxDeviceBackend.DefaultRoot, string readOnlyRulePath = null)
        {
            this.sysfsRoot = sysfsRoot;
            this.readOnlyRulePath = readOnlyRulePath;
        }

        public override IDeviceBackend CreateBackend()
        {
            backend = new LinuxDeviceBackend(sysfsRoot);
            return backend;
        }

        public override IReadOnlyPolicy CreateReadOnly()
        {
            return readOnlyRulePath == null ? new LinuxReadOnly() : new LinuxReadOnly(readOnlyRulePath, null);
        }

        public override IInstallGate CreateInstallGate(IDeviceBackend deviceBackend)
        {
            var linux = deviceBackend as LinuxDeviceBackend;
            if (linux == null) return new NoInstallGate();
            return new LinuxUsbGate(linux, Path.Combine(LotusPaths.SecureDir, "controllers.saved"));
        }

        public override IControlServer CreateControlServer(string name,
            Func<Dictionary<string, object>, Dictionary<string, object>> handler, Action<string> onProblem,
            bool restricted)
        {
            return new UnixControlServer(name, handler, onProblem, restricted);
        }

        public override IDisposable WatchDevices(Action<int, string> onChange)
        {
            var watcher = new UeventWatcher(
                arrived => onChange(arrived ? DeviceChange.Enumerated : DeviceChange.Removed, null), sysfsRoot);
            watcher.Start();
            return watcher;
        }

        [DllImport("libc")]
        private static extern uint umask(uint mask);

        /// <summary>
        /// Everything LOTUS keeps is root's alone: the folders, the store, the
        /// log. Nothing on Linux reads them but the daemon and the "lotus"
        /// command, and both run as root.
        /// </summary>
        public override void PrepareFolders(Action<string> onError)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    // Every file the daemon creates from here on is created closed to everyone else.
                    try
                    {
                        umask(0x3F); // 077
                    }
                    catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
                    {
                    }
                }
                Directory.CreateDirectory(LotusPaths.DataDir);
                Directory.CreateDirectory(LotusPaths.SecureDir);
                if (OperatingSystem.IsWindows()) return;
                const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                foreach (string folder in new[] { LotusPaths.DataDir, LotusPaths.SecureDir })
                {
                    File.SetUnixFileMode(folder, ownerOnly | UnixFileMode.UserExecute);
                    foreach (string file in Directory.GetFiles(folder))
                    {
                        File.SetUnixFileMode(file, ownerOnly);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Directory.CreateDirectory(LotusPaths.DataDir);
                Directory.CreateDirectory(LotusPaths.SecureDir);
                onError("The permissions on the LOTUS state folder could not be set. " + ex.Message);
            }
        }

        public override void ConfigureStore(SecureStore store)
        {
            store.ProtectKeyFile = path =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            };
        }

        /// <summary>
        /// The installer leaves a note in the state folder when it finds LOTUS
        /// locked down. Only root can write there, so the note is root's word.
        /// </summary>
        public const string ResetNoteName = "reset.request";

        public override bool TakeResetRequest()
        {
            string note = Path.Combine(LotusPaths.DataDir, ResetNoteName);
            try
            {
                if (!File.Exists(note)) return false;
                File.Delete(note);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        public override void LedgerLoaded(Ledger ledger)
        {
            if (backend != null) backend.IsBlockedByLotus = ledger.Contains;
        }
    }
}
