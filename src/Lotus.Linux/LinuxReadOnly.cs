using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Lotus.Core;

namespace Lotus.Linux
{
    /// <summary>What a udev rule can tell one USB device from another by.</summary>
    public sealed class UsbIdentity
    {
        /// <summary>Four hex digits in small letters, the way the kernel writes them.</summary>
        public string Vid = "";
        public string Pid = "";
        /// <summary>The serial number exactly as the device gives it, or empty.</summary>
        public string RawSerial = "";
    }

    /// <summary>
    /// Read-only mode on Linux. A udev rule marks every USB disk as read-only in
    /// the kernel the moment it appears, before the desktop gets to mount it. The
    /// kernel itself then refuses writes, whatever a mount command asks for.
    ///
    /// The disk the system runs from is left out of the rule by name. A system
    /// that is installed on a USB disk, or runs live from a stick, would
    /// otherwise lose its own disk the next time it starts.
    ///
    /// The rule is a file of its own, so LOTUS removes exactly what it added.
    /// Disks that are connected when the mode changes are switched off and on by
    /// the enforcer, which makes them appear again under the new rule.
    /// </summary>
    public sealed class LinuxReadOnly : IReadOnlyPolicy
    {
        public const string DefaultRulePath = "/etc/udev/rules.d/99-lotus-readonly.rules";

        private const string Header =
            "# Written by LOTUS while removable storage is set to read-only. Do not edit: LOTUS removes this file again.\n";

        private readonly string rulePath;
        private readonly Action reload;
        private readonly Func<List<UsbIdentity>> systemDisks;

        /// <param name="systemDisks">
        /// The USB disks the system runs from. It throws when that cannot be
        /// worked out, and then read-only mode is not switched on at all.
        /// </param>
        public LinuxReadOnly(Func<List<UsbIdentity>> systemDisks) : this(DefaultRulePath, ReloadUdev, systemDisks)
        {
        }

        /// <summary>Tests put the rule somewhere harmless and skip the reload.</summary>
        public LinuxReadOnly(string rulePath, Action reload, Func<List<UsbIdentity>> systemDisks = null)
        {
            this.rulePath = rulePath;
            this.reload = reload ?? (() => { });
            this.systemDisks = systemDisks ?? (() => new List<UsbIdentity>());
        }

        /// <summary>
        /// The text of the rule. Every USB disk and every partition on one is set
        /// read-only, except those that sit on one of the given devices.
        /// </summary>
        public static string BuildRule(IEnumerable<UsbIdentity> keepWritable)
        {
            var rule = new StringBuilder(Header);
            rule.Append("ACTION!=\"add|change\", GOTO=\"lotus_readonly_end\"\n");
            rule.Append("SUBSYSTEM!=\"block\", GOTO=\"lotus_readonly_end\"\n");

            var lines = new List<string>();
            foreach (UsbIdentity disk in keepWritable ?? new List<UsbIdentity>())
            {
                if (!IsHex4(disk.Vid) || !IsHex4(disk.Pid)) continue;
                string line = "ATTRS{idVendor}==\"" + disk.Vid + "\", ATTRS{idProduct}==\"" + disk.Pid + "\"";
                // A serial number comes from the device. It goes into the rule only
                // when it is made of characters that mean nothing to udev. Without
                // it the rule spares every unit of that model, which errs on the
                // side of a system that still starts.
                if (IsPlain(disk.RawSerial)) line += ", ATTRS{serial}==\"" + disk.RawSerial + "\"";
                line += ", GOTO=\"lotus_readonly_end\"\n";
                if (!lines.Contains(line)) lines.Add(line);
            }
            lines.Sort(StringComparer.Ordinal);
            if (lines.Count > 0) rule.Append("# The system runs from this disk, so it stays writable.\n");
            foreach (string line in lines) rule.Append(line);

            rule.Append("SUBSYSTEMS==\"usb\", RUN+=\"/sbin/blockdev --setro /dev/%k\"\n");
            rule.Append("LABEL=\"lotus_readonly_end\"\n");
            return rule.ToString();
        }

        private static bool IsHex4(string text)
        {
            if (text == null || text.Length != 4) return false;
            foreach (char c in text)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        private static bool IsPlain(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 128) return false;
            foreach (char c in text)
            {
                bool plain = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                             c == '.' || c == '_' || c == '-';
                if (!plain) return false;
            }
            return true;
        }

        /// <summary>
        /// True when the rule is in place and still spares the right disks. When
        /// the system's own disk cannot be worked out right now, a rule that is
        /// there is left as it is.
        /// </summary>
        public bool IsApplied()
        {
            if (!File.Exists(rulePath)) return false;
            string text = File.ReadAllText(rulePath);
            try
            {
                return text == BuildRule(systemDisks());
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                return text.StartsWith(Header, StringComparison.Ordinal);
            }
        }

        public void Apply()
        {
            List<UsbIdentity> keep;
            try
            {
                keep = systemDisks();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "LOTUS could not work out which disk the system runs from, so it leaves every disk writable. " + ex.Message);
            }
            string directory = Path.GetDirectoryName(rulePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(rulePath, BuildRule(keep));
            if (!OperatingSystem.IsWindows())
            {
                // The daemon creates its own files closed to everyone. A udev rule is
                // not a secret, and the tools that check rules expect to be able to read it.
                File.SetUnixFileMode(rulePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            reload();
        }

        public void Remove()
        {
            if (File.Exists(rulePath)) File.Delete(rulePath);
            reload();
        }

        private static void ReloadUdev()
        {
            try
            {
                var start = new ProcessStartInfo("udevadm", "control --reload")
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (Process process = Process.Start(start))
                {
                    process.WaitForExit(10000);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                // udev also notices new rule files by itself.
            }
        }
    }
}
