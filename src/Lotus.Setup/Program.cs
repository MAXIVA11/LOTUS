using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace Lotus.Setup
{
    public static class Program
    {
        private static readonly List<string> LogLines = new List<string>();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetDefaultDllDirectories(uint flags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectoryW(string path);

        /// <summary>
        /// Setup runs with administrator rights, usually from a Downloads folder
        /// that other programs can write to. This tells Windows to load system
        /// libraries from the Windows folder only, so a library planted next to
        /// setup is not picked up.
        /// </summary>
        private static void RestrictLibrarySearch()
        {
            const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;
            try
            {
                SetDllDirectoryW("");
                SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
            }
            catch (EntryPointNotFoundException)
            {
                // A Windows version without this call. Nothing more can be done here.
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            RestrictLibrarySearch();
            return Run(args);
        }

        // Kept out of Main, so that nothing WPF needs is loaded before the library
        // search has been restricted.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            bool quiet = false, uninstall = false;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                // Developer option: draw the setup screens into PNG files and exit.
                if (args[i].TrimStart('/', '-').Equals("shots", StringComparison.OrdinalIgnoreCase))
                {
                    return SetupWindow.RenderScreens(args[i + 1], CreateApp());
                }
                // Developer option: write a shortcut the same way the setup window
                // does, from its background thread, into the given folder.
                if (args[i].TrimStart('/', '-').Equals("shortcut-test", StringComparison.OrdinalIgnoreCase))
                {
                    string link = Path.Combine(args[i + 1], "LOTUS test.lnk");
                    string failure = SetupWindow.RunInBackground(() =>
                        Shortcut.Create(link, Assembly.GetExecutingAssembly().Location, "--tray", "test")).Result;
                    if (failure != null) WriteLog("shortcut-test: " + failure);
                    return failure == null && File.Exists(link) ? 0 : 1;
                }
                // Developer option: treat the given folder as the install folder and
                // remove it the way the uninstaller removes the real one.
                if (args[i].TrimStart('/', '-').Equals("remove-folder-test", StringComparison.OrdinalIgnoreCase))
                {
                    Installer.OverrideInstallDir(args[i + 1]);
                    var installer = new Installer(WriteLog);
                    installer.RemoveProgramFiles();
                    if (Installer.RemoveInstallFolderAtExit) RemoveInstallFolderLater();
                    return installer.Warnings.Count == 0 ? 0 : 2;
                }
            }
            foreach (string arg in args)
            {
                switch (arg.TrimStart('/', '-').ToLowerInvariant())
                {
                    case "quiet":
                    case "q":
                    case "silent":
                        quiet = true;
                        break;
                    case "uninstall":
                        uninstall = true;
                        break;
                }
            }

            string self = Assembly.GetExecutingAssembly().Location;
            // The copy in the install folder is the uninstaller, also when double-clicked.
            if (string.Equals(Path.GetFileName(self), Installer.UninstallExe, StringComparison.OrdinalIgnoreCase))
            {
                uninstall = true;
            }

            int code = 1;
            try
            {
                code = quiet ? RunQuiet(uninstall) : RunWindow(uninstall);
                return code;
            }
            finally
            {
                SaveLog(uninstall, code);
                // A program cannot delete itself while it runs. When the uninstaller ran
                // from the install folder, a helper removes the folder once this process is gone.
                if (Installer.RemoveInstallFolderAtExit) RemoveInstallFolderLater();
            }
        }

        private static int RunQuiet(bool uninstall)
        {
            var installer = new Installer(WriteLog);
            try
            {
                WriteLog(uninstall ? "=== quiet uninstall ===" : "=== quiet install ===");
                if (uninstall) installer.Uninstall();
                else installer.Install();
                return installer.Warnings.Count == 0 ? 0 : 2;
            }
            catch (Exception ex)
            {
                WriteLog("FAILED: " + ex.Message);
                return 1;
            }
        }

        private static Application CreateApp()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            string assembly = Assembly.GetExecutingAssembly().GetName().Name;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/" + assembly + ";component/Theme/Theme.xaml", UriKind.Relative)
            });
            return app;
        }

        private static int RunWindow(bool uninstall)
        {
            Application app = CreateApp();
            var window = new SetupWindow(uninstall, WriteLog);
            app.Run(window);
            return window.ExitCode;
        }

        public static void WriteLog(string line)
        {
            lock (LogLines) LogLines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line);
        }

        /// <summary>
        /// Writes what setup did into the install folder, which only administrators
        /// can write to. A removal that went well leaves no log behind, because it
        /// leaves nothing behind.
        /// </summary>
        private static void SaveLog(bool uninstall, int exitCode)
        {
            try
            {
                if (uninstall && exitCode == 0) return;
                if (!Directory.Exists(Installer.InstallDir)) return;
                string text;
                lock (LogLines)
                {
                    if (LogLines.Count == 0) return;
                    text = string.Join(Environment.NewLine, LogLines) + Environment.NewLine;
                }
                File.AppendAllText(Path.Combine(Installer.InstallDir, Installer.SetupLog), text, new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // The log is a convenience. Setup goes on without it.
            }
        }

        /// <summary>
        /// Starts a hidden command prompt that waits a few seconds and then deletes
        /// the install folder, which by now only holds this program.
        /// </summary>
        private static void RemoveInstallFolderLater()
        {
            try
            {
                string folder = Path.GetFullPath(Installer.InstallDir).TrimEnd(Path.DirectorySeparatorChar);
                // Only ever delete a folder that is really called LOTUS and sits inside another folder.
                if (!string.Equals(Path.GetFileName(folder), Installer.ProductName, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(Path.GetDirectoryName(folder)) || folder.IndexOf('"') >= 0)
                {
                    return;
                }
                string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                Process.Start(new ProcessStartInfo(cmd,
                    "/c ping 127.0.0.1 -n 4 > nul & rmdir /s /q \"" + folder + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Environment.SystemDirectory
                });
            }
            catch (Exception ex)
            {
                WriteLog("Could not schedule the removal of the install folder: " + ex.Message);
            }
        }
    }
}
