using System;
using System.Diagnostics;
using System.IO;
using Lotus.Core;

namespace Lotus.Linux
{
    /// <summary>
    /// Read-only mode on Linux. A udev rule marks every USB disk as read-only in
    /// the kernel the moment it appears, before the desktop gets to mount it. The
    /// kernel itself then refuses writes, whatever a mount command asks for.
    ///
    /// The rule is a file of its own, so LOTUS removes exactly what it added.
    /// Disks that are connected when the mode changes are switched off and on by
    /// the enforcer, which makes them appear again under the new rule.
    /// </summary>
    public sealed class LinuxReadOnly : IReadOnlyPolicy
    {
        public const string DefaultRulePath = "/etc/udev/rules.d/99-lotus-readonly.rules";

        private const string Rule =
            "# Written by LOTUS while removable storage is set to read-only. Do not edit: LOTUS removes this file again.\n" +
            "ACTION==\"add|change\", SUBSYSTEM==\"block\", ENV{ID_BUS}==\"usb\", RUN+=\"/bin/sh -c 'blockdev --setro /dev/%k'\"\n";

        private readonly string rulePath;
        private readonly Action reload;

        public LinuxReadOnly() : this(DefaultRulePath, ReloadUdev)
        {
        }

        /// <summary>Tests put the rule somewhere harmless and skip the reload.</summary>
        public LinuxReadOnly(string rulePath, Action reload)
        {
            this.rulePath = rulePath;
            this.reload = reload ?? (() => { });
        }

        public bool IsApplied()
        {
            return File.Exists(rulePath) && File.ReadAllText(rulePath) == Rule;
        }

        public void Apply()
        {
            string directory = Path.GetDirectoryName(rulePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(rulePath, Rule);
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
