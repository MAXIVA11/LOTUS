using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.Win32;

namespace Lotus.Setup
{
    /// <summary>Thrown when a step fails in a way that makes going on pointless.</summary>
    public sealed class SetupException : Exception
    {
        public SetupException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Installs and removes LOTUS. Every step reports what it is doing through the
    /// log callback, so the window and the log file say the same thing.
    /// </summary>
    public sealed class Installer
    {
        public const string ProductName = "LOTUS";
        public const string ServiceName = "LotusGuard";
        public const string ServiceDisplayName = "LOTUS Guard";
        public const string ServiceDescription =
            "Enforces the LOTUS policy for USB devices and removable storage on this computer.";
        public const string AppExe = "LOTUS.exe";
        public const string GuardExe = "LotusGuard.exe";
        public const string UninstallExe = "Uninstall.exe";
        public const string NotifierLink = "LOTUS Notifier.lnk";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\LOTUS";
        private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        private readonly Action<string> log;

        /// <summary>Things that went wrong without stopping the run. Shown at the end.</summary>
        public readonly List<string> Warnings = new List<string>();

        /// <summary>
        /// Set when the uninstaller ran from inside the install folder. Its own file
        /// and the folder are then removed by a helper after the process has ended.
        /// </summary>
        public static bool RemoveInstallFolderAtExit;

        public Installer(Action<string> log)
        {
            this.log = log ?? (_ => { });
        }

        private static string installDirOverride;

        public static string InstallDir => installDirOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);

        /// <summary>Points setup at another folder. Only the developer self-test uses this.</summary>
        internal static void OverrideInstallDir(string folder)
        {
            installDirOverride = Path.GetFullPath(folder);
        }

        public static string DataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);

        private static string StartMenuLink =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "LOTUS.lnk");

        private static string DesktopLink =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "LOTUS.lnk");

        public static string Version
        {
            get
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, AppExe));

        public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") != null;

        // ================= install =================

        public void Install()
        {
            if (!HasPayload)
            {
                throw new SetupException("This copy of LOTUS-Setup was built without the program files. Build it with build.ps1.");
            }

            bool update = IsInstalled;
            log(update ? "Updating LOTUS in " + InstallDir : "Installing LOTUS to " + InstallDir);

            StopService();
            StopRunningCopies();
            CancelPendingDeletes();
            CopyFiles();
            PrepareDataFolder();
            RegisterService();
            CreateShortcuts();
            RegisterNotifier();
            RegisterUninstaller();
            StartService();
            StartNotifier();

            log("Done. LOTUS is installed and disarmed. Nothing is blocked until you switch it on.");
        }

        /// <summary>
        /// Windows keeps a list of files to delete at the next restart. If a file in
        /// the install folder is still on that list, the restart would delete part
        /// of this fresh install. Take those entries off the list.
        /// </summary>
        private void CancelPendingDeletes()
        {
            const string keyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";
            const string valueName = "PendingFileRenameOperations";
            try
            {
                using (RegistryKey machine = OpenMachine())
                using (RegistryKey key = machine.OpenSubKey(keyPath, true))
                {
                    if (key == null || !(key.GetValue(valueName) is string[] entries)) return;
                    string[] kept = PendingDeletes.Without(entries, InstallDir, out int removed);
                    if (removed == 0) return;
                    if (kept.Length == 0) key.DeleteValue(valueName, false);
                    else key.SetValue(valueName, kept, RegistryValueKind.MultiString);
                    log("Cancelled " + removed + " leftover delete request(s) for the install folder");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.SecurityException || ex is ArgumentException)
            {
                Warn("Could not check for leftover delete requests. " + ex.Message);
            }
        }

        private void CopyFiles()
        {
            log("Copying program files");
            try
            {
                Directory.CreateDirectory(InstallDir);
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    string root = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue; // a folder
                        string target = Path.GetFullPath(Path.Combine(InstallDir, entry.FullName));
                        // Never write outside the install folder, whatever the archive says.
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        WriteWithRetry(entry, target);
                    }
                }

                string self = Assembly.GetExecutingAssembly().Location;
                string uninstaller = Path.Combine(InstallDir, UninstallExe);
                if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller),
                        StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(self, uninstaller, true);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new SetupException("Could not copy the program files. " + ex.Message, ex);
            }
        }

        private static void WriteWithRetry(ZipArchiveEntry entry, string target)
        {
            // A file can stay locked for a moment after its process ends.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    entry.ExtractToFile(target, true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(300);
                }
            }
        }

        /// <summary>
        /// The data folder holds the policy. Administrators and SYSTEM may change it.
        /// Everyone else may only read, which the notifier needs for the activity log.
        /// </summary>
        private void PrepareDataFolder()
        {
            log("Protecting the policy folder");
            try
            {
                Directory.CreateDirectory(DataDir);
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                Directory.SetAccessControl(DataDir, security);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException)
            {
                throw new SetupException("Could not protect the policy folder. " + ex.Message, ex);
            }
        }

        private void RegisterService()
        {
            log("Registering the LOTUS Guard service");
            try
            {
                ServiceControl.CreateOrUpdate(ServiceName, ServiceDisplayName, ServiceDescription,
                    Path.Combine(InstallDir, GuardExe));
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new SetupException(ex.Message, ex);
            }

            // If the service ever crashes, Windows starts it again.
            int code = Run(Path.Combine(Environment.SystemDirectory, "sc.exe"),
                "failure " + ServiceName + " reset= 86400 actions= restart/5000/restart/5000/restart/60000", 20000);
            if (code != 0) Warn("Could not set the service to restart after a crash (sc.exe returned " + code + ").");
        }

        private void CreateShortcuts()
        {
            log("Creating shortcuts");
            string app = Path.Combine(InstallDir, AppExe);
            try
            {
                Shortcut.Create(StartMenuLink, app, "", "LOTUS, Local Operational Trusted USB System");
                Shortcut.Create(DesktopLink, app, "", "LOTUS, Local Operational Trusted USB System");
                Shortcut.Create(Path.Combine(InstallDir, NotifierLink), app, "--tray", "LOTUS notifier");
            }
            catch (Exception ex)
            {
                Warn("Could not create every shortcut. " + ex.Message + " You can start LOTUS from " + app + ".");
            }
        }

        private void RegisterNotifier()
        {
            try
            {
                using (RegistryKey machine = OpenMachine())
                using (RegistryKey run = machine.CreateSubKey(RunKey))
                {
                    run.SetValue(ProductName, "\"" + Path.Combine(InstallDir, AppExe) + "\" --tray",
                        RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                Warn("Could not set the notifier to start at sign-in. " + ex.Message);
            }
        }

        private void RegisterUninstaller()
        {
            log("Adding LOTUS to the list of installed apps");
            try
            {
                string uninstaller = Path.Combine(InstallDir, UninstallExe);
                using (RegistryKey machine = OpenMachine())
                using (RegistryKey key = machine.CreateSubKey(UninstallKey))
                {
                    key.SetValue("DisplayName", ProductName);
                    key.SetValue("DisplayVersion", Version);
                    key.SetValue("Publisher", "LOTUS project");
                    key.SetValue("DisplayIcon", Path.Combine(InstallDir, AppExe));
                    key.SetValue("InstallLocation", InstallDir);
                    key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                    key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /quiet");
                    key.SetValue("URLInfoAbout", "https://github.com/MAXIVA11/LOTUS");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    key.SetValue("EstimatedSize", (int)(FolderSize(InstallDir) / 1024), RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Warn("Could not add LOTUS to the list of installed apps. " + ex.Message +
                     " To remove it later, run " + Path.Combine(InstallDir, UninstallExe) + " /uninstall.");
            }
        }

        private void StartService()
        {
            log("Starting LOTUS Guard");
            try
            {
                using (var controller = new ServiceController(ServiceName))
                {
                    if (controller.Status != ServiceControllerStatus.Running &&
                        controller.Status != ServiceControllerStatus.StartPending)
                    {
                        controller.Start();
                    }
                    controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException ||
                                       ex is System.ServiceProcess.TimeoutException)
            {
                string detail = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                throw new SetupException("LOTUS Guard was installed but did not start. " + detail, ex);
            }
        }

        /// <summary>
        /// Starts the notifier for the person who is signed in. Setup itself runs with
        /// administrator rights, and the notifier must not, so Explorer starts it.
        /// </summary>
        private void StartNotifier()
        {
            try
            {
                string link = Path.Combine(InstallDir, NotifierLink);
                if (!File.Exists(link)) return;
                Process.Start(new ProcessStartInfo(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    "\"" + link + "\"") { UseShellExecute = false });
            }
            catch (Exception)
            {
                // It starts at the next sign-in anyway.
            }
        }

        // ================= uninstall =================

        public void Uninstall()
        {
            log("Removing LOTUS");

            StopService();
            ReleaseDevices();
            DeleteService();
            StopRunningCopies();
            RemoveRegistration();
            RemoveShortcuts();
            RemoveProgramFiles();
            RemoveFolder(DataDir, "policy and activity log");

            log(Warnings.Count == 0 ? "Done. LOTUS is removed." : "LOTUS is removed, with warnings.");
        }

        /// <summary>Switches back on every device LOTUS blocked and lifts read-only mode.</summary>
        private void ReleaseDevices()
        {
            string guard = Path.Combine(InstallDir, GuardExe);
            if (!File.Exists(guard))
            {
                Warn("LotusGuard.exe is missing, so blocked devices could not be switched back on. " +
                     "Enable them in Device Manager if any stay off.");
                return;
            }
            log("Switching blocked devices back on");
            int code = Run(guard, "--release-all", 120000);
            if (code != 0)
            {
                Warn("Some blocked devices could not be switched back on. " +
                     "Open Device Manager, look for devices marked with a down arrow, and choose Enable device.");
            }
        }

        private void DeleteService()
        {
            log("Removing the LOTUS Guard service");
            try
            {
                ServiceControl.Delete(ServiceName);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                Warn(ex.Message);
            }
        }

        private void RemoveRegistration()
        {
            try
            {
                using (RegistryKey machine = OpenMachine())
                {
                    using (RegistryKey run = machine.OpenSubKey(RunKey, true))
                    {
                        run?.DeleteValue(ProductName, false);
                    }
                    machine.DeleteSubKeyTree(UninstallKey, false);
                }
            }
            catch (Exception ex)
            {
                Warn("Could not remove every registry entry. " + ex.Message);
            }
        }

        private void RemoveShortcuts()
        {
            log("Removing shortcuts");
            foreach (string link in new[] { StartMenuLink, DesktopLink })
            {
                try
                {
                    if (File.Exists(link)) File.Delete(link);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Warn("Could not delete " + link + ". " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Deletes the program files. The uninstaller usually runs from this very
        /// folder and cannot delete itself, so its own file is left for a helper that
        /// Program starts on exit.
        /// </summary>
        internal void RemoveProgramFiles()
        {
            if (!Directory.Exists(InstallDir)) return;
            log("Deleting the program files");

            string root = Path.GetFullPath(InstallDir).TrimEnd(Path.DirectorySeparatorChar) +
                          Path.DirectorySeparatorChar;
            string self = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
            bool selfInside = self.StartsWith(root, StringComparison.OrdinalIgnoreCase);

            int stuck = 0;
            try
            {
                foreach (string file in Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories))
                {
                    if (selfInside && string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!DeleteFile(file)) stuck++;
                }
                string[] folders = Directory.GetDirectories(InstallDir, "*", SearchOption.AllDirectories);
                Array.Sort(folders, (a, b) => b.Length.CompareTo(a.Length));
                foreach (string folder in folders)
                {
                    try { Directory.Delete(folder); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                stuck++;
            }

            if (stuck > 0)
            {
                Warn("Some files in " + InstallDir + " are in use and could not be deleted. " +
                     "Delete the folder by hand once they are free.");
            }

            if (selfInside)
            {
                RemoveInstallFolderAtExit = true;
                return;
            }
            try
            {
                Directory.Delete(InstallDir, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (stuck == 0) Warn("Could not delete " + InstallDir + ". " + ex.Message);
            }
        }

        private static bool DeleteFile(string file)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Thread.Sleep(300);
                }
            }
            return false;
        }

        private void RemoveFolder(string folder, string what)
        {
            if (!Directory.Exists(folder)) return;
            log("Deleting the " + what);
            Exception last = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    Directory.Delete(folder, true);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(400);
                }
            }
            Warn("Could not delete " + folder + ". " + (last != null ? last.Message : "") +
                 " Delete the folder by hand.");
        }

        // ================= shared steps =================

        private void StopService()
        {
            try
            {
                if (!ServiceControl.Exists(ServiceName)) return;
                using (var controller = new ServiceController(ServiceName))
                {
                    if (controller.Status == ServiceControllerStatus.Stopped) return;
                    log("Stopping LOTUS Guard");
                    if (controller.Status != ServiceControllerStatus.StopPending) controller.Stop();
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(40));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException ||
                                       ex is System.ServiceProcess.TimeoutException ||
                                       ex is System.ComponentModel.Win32Exception)
            {
                throw new SetupException("Could not stop the LOTUS Guard service. " + ex.Message, ex);
            }
        }

        /// <summary>Closes the app and the notifier so their files can be replaced or deleted.</summary>
        private void StopRunningCopies()
        {
            string dir = Path.GetFullPath(InstallDir) + Path.DirectorySeparatorChar;
            foreach (string name in new[] { "LOTUS", "LotusGuard" })
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string path = process.MainModule.FileName;
                        if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                        process.Kill();
                        process.WaitForExit(8000);
                    }
                    catch (Exception)
                    {
                        // Already gone, or not ours to inspect.
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private int Run(string file, string arguments, int timeoutMs)
        {
            try
            {
                using (Process process = Process.Start(new ProcessStartInfo(file, arguments)
                       {
                           UseShellExecute = false,
                           CreateNoWindow = true,
                           WindowStyle = ProcessWindowStyle.Hidden
                       }))
                {
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return -1;
                    }
                    return process.ExitCode;
                }
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private void Warn(string message)
        {
            Warnings.Add(message);
            log("Warning: " + message);
        }

        private static RegistryKey OpenMachine()
        {
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        }

        private static long FolderSize(string folder)
        {
            long total = 0;
            try
            {
                foreach (string file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                }
            }
            catch (IOException)
            {
            }
            return total;
        }
    }
}
