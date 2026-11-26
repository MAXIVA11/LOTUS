using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;

namespace Lotus.Setup
{
    public static class Program
    {
        private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "LOTUS-Setup.log");

        [STAThread]
        public static int Main(string[] args)
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

            try
            {
                return quiet ? RunQuiet(uninstall) : RunWindow(uninstall);
            }
            finally
            {
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
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
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
