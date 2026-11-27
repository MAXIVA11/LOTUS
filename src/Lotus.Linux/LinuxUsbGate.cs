using System;
using System.Collections.Generic;
using System.IO;
using Lotus.Core;

namespace Lotus.Linux
{
    /// <summary>
    /// Strict mode on Linux. Every USB controller has a file named
    /// "authorized_default". While it holds 0, the kernel does not switch on any
    /// device that is plugged into that controller: it reads the device's
    /// descriptors and then waits. LOTUS switches on the devices the policy
    /// allows, one by one. A device the policy blocks never runs.
    ///
    /// The values the controllers had before are kept in a small file, so that
    /// LOTUS can tell its own setting from one an administrator or another
    /// program made, and can put things back the way they were.
    /// </summary>
    public sealed class LinuxUsbGate : IInstallGate, IReportsAtStart
    {
        /// <summary>Where the kernel shows the setting it was started with: usbcore.authorized_default.</summary>
        public const string KernelSetting = "/sys/module/usbcore/parameters/authorized_default";

        private readonly LinuxDeviceBackend backend;
        private readonly string savedPath;
        private readonly string kernelSettingPath;

        /// <param name="savedPath">Where the earlier values are kept while LOTUS holds the controllers shut.</param>
        /// <param name="kernelSettingPath">The kernel's own setting. Left empty, LOTUS never assumes the kernel held anything back.</param>
        public LinuxUsbGate(LinuxDeviceBackend backend, string savedPath, string kernelSettingPath = null)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.savedPath = savedPath ?? throw new ArgumentNullException(nameof(savedPath));
            this.kernelSettingPath = kernelSettingPath;
        }

        public string StartNote { get; private set; }
        public bool StartNoteIsProblem { get; private set; }

        /// <summary>
        /// True when the kernel was started with usbcore.authorized_default=2. It
        /// then switches on only the devices built into the computer and leaves
        /// every other one waiting, from the first moment of the start. That
        /// closes the time before the daemon runs, and it makes the daemon the
        /// one who has to switch those devices on.
        /// </summary>
        public bool KernelHoldsAtStart => HoldsAtStart(kernelSettingPath);

        public static bool HoldsAtStart(string kernelSettingPath)
        {
            try
            {
                return !string.IsNullOrEmpty(kernelSettingPath) && File.Exists(kernelSettingPath) &&
                       File.ReadAllText(kernelSettingPath).Trim() == "2";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Called when the daemon starts, and again before every look at the
        /// devices, which costs a few small reads. Controllers the kernel left in
        /// its waiting state become LOTUS's own: they are shut the way strict
        /// mode shuts them, with "open" noted as the state to go back to. The
        /// first pass then does the rest with what is there already. A waiting
        /// device the policy blocks goes on the list of blocked devices without
        /// ever having run. If the policy does not ask for strict mode, the
        /// controllers are opened again and the devices that are still waiting
        /// are switched on. If it does, they are let in one by one.
        /// </summary>
        public void TakeOverAtStart()
        {
            if (!KernelHoldsAtStart) return;
            try
            {
                var waiting = new List<string>();
                foreach (string controller in backend.Controllers())
                {
                    if (Read(controller) == "2") waiting.Add(controller);
                }
                if (waiting.Count == 0) return;
                Dictionary<string, string> saved = ReadSaved();
                foreach (string controller in waiting) saved[controller] = "1";
                WriteSaved(saved);
                foreach (string controller in waiting) Write(controller, "0");
                if (StartNote == null)
                {
                    StartNote = "The kernel was started with usbcore.authorized_default=2 and held back every USB device " +
                                "that is not built in. LOTUS decides about each of them now.";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                StartNoteIsProblem = true;
                StartNote = "The kernel held back USB devices at the start, and LOTUS could not take the controllers over. " +
                            ex.Message + " Devices that are not built in may stay off.";
            }
        }

        public bool Supported
        {
            get
            {
                List<string> controllers = backend.Controllers();
                if (controllers.Count == 0) return false;
                foreach (string controller in controllers)
                {
                    if (!File.Exists(backend.ControllerFile(controller, "authorized_default"))) return false;
                }
                return true;
            }
        }

        public bool Owned => File.Exists(savedPath);

        /// <summary>Some controller is shut although LOTUS did not shut it. USBGuard does that, for one.</summary>
        public bool Foreign
        {
            get
            {
                if (Owned) return false;
                foreach (string controller in backend.Controllers())
                {
                    if (Read(controller) == "0") return true;
                }
                return false;
            }
        }

        /// <summary>
        /// The plan's lists mean nothing here: the kernel has one switch per
        /// controller, and the exceptions are made device by device. What counts
        /// is that every controller is shut, including one that appeared later.
        /// </summary>
        public bool Matches(InstallPlan plan)
        {
            if (!Owned) return false;
            foreach (string controller in backend.Controllers())
            {
                if (Read(controller) != "0") return false;
            }
            return true;
        }

        public void Apply(InstallPlan plan)
        {
            if (Foreign)
            {
                throw new InvalidOperationException("The USB controllers are already held shut by something other than LOTUS.");
            }
            Dictionary<string, string> saved = ReadSaved();
            List<string> controllers = backend.Controllers();
            foreach (string controller in controllers)
            {
                if (!saved.ContainsKey(controller)) saved[controller] = Earlier(Read(controller));
            }
            // The note first, so that a stop halfway never leaves a shut controller nobody remembers.
            WriteSaved(saved);
            foreach (string controller in controllers) Write(controller, "0");
        }

        /// <summary>Exceptions are made by switching the device on, which the backend does.</summary>
        public void AllowInstance(string instanceId)
        {
        }

        public void Remove()
        {
            if (!Owned) return;
            Dictionary<string, string> saved = ReadSaved();
            foreach (string controller in backend.Controllers())
            {
                string before = Earlier(saved.TryGetValue(controller, out string value) ? value : "1");
                Write(controller, before);
            }
            File.Delete(savedPath);
        }

        /// <summary>
        /// The state a controller goes back to. LOTUS never leaves a controller
        /// shut that it shut itself. And where the kernel holds devices back at
        /// the start for LOTUS, the waiting state is not one to return to either:
        /// nothing would switch on what is plugged in afterwards.
        /// </summary>
        private string Earlier(string value)
        {
            if (value == null || value == "0") return "1";
            if (value == "2" && KernelHoldsAtStart) return "1";
            return value;
        }

        private string Read(string controller)
        {
            try
            {
                string path = backend.ControllerFile(controller, "authorized_default");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void Write(string controller, string value)
        {
            File.WriteAllText(backend.ControllerFile(controller, "authorized_default"), value);
        }

        private Dictionary<string, string> ReadSaved()
        {
            var saved = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(savedPath)) return saved;
            foreach (string line in File.ReadAllLines(savedPath))
            {
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string value = line.Substring(equals + 1).Trim();
                if (value == "0" || value == "1" || value == "2" || value == "-1") saved[line.Substring(0, equals).Trim()] = value;
            }
            return saved;
        }

        private void WriteSaved(Dictionary<string, string> saved)
        {
            string directory = Path.GetDirectoryName(savedPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var lines = new List<string>();
            foreach (KeyValuePair<string, string> pair in saved) lines.Add(pair.Key + "=" + pair.Value);
            lines.Sort(StringComparer.Ordinal);
            File.WriteAllLines(savedPath, lines);
        }
    }
}
