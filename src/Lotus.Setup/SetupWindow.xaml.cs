using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Lotus.Setup
{
    public partial class SetupWindow : Window
    {
        private enum Stage
        {
            Welcome,
            Working,
            Done,
            Failed
        }

        private readonly bool uninstall;
        private readonly Action<string> fileLog;
        private readonly ObservableCollection<string> lines = new ObservableCollection<string>();
        private Stage stage = Stage.Welcome;
        private bool passwordNeeded;

        /// <summary>0 when everything worked, 1 after a failure, 2 after warnings, 3 when cancelled.</summary>
        public int ExitCode { get; private set; } = 3;

        public SetupWindow(bool uninstall, Action<string> fileLog)
        {
            this.uninstall = uninstall;
            this.fileLog = fileLog ?? (_ => { });
            InitializeComponent();
            LogList.ItemsSource = lines;
            VersionText.Text = "v" + Installer.Version;
            Closing += OnClosing;
            ShowWelcome();
        }

        private void ShowWelcome()
        {
            if (uninstall)
            {
                WelcomeTitle.Text = "REMOVE";
                WelcomeIntro.Text = "This removes LOTUS from this PC.";
                WelcomeList.ItemsSource = new[]
                {
                    "Every device LOTUS blocked is switched back on.",
                    "Removable storage becomes writable again.",
                    "The policy, the trusted list and the activity log are deleted."
                };
                WelcomeNote.Text = "After this, any USB device works on this PC again.";
                WelcomePath.Text = "Removes " + Installer.InstallDir;
                PrimaryButton.Content = "REMOVE";
                PrimaryButton.Style = (Style)FindResource("Btn.Danger");
                AskForPasswordIfSet(false);
                return;
            }

            bool update = Installer.IsInstalled;
            WelcomeTitle.Text = update ? "UPDATE" : "INSTALL";
            WelcomeIntro.Text = update
                ? "LOTUS is already on this PC. This replaces it with version " + Installer.Version +
                  " and keeps your policy and trusted devices."
                : "This puts three things on this PC:";
            WelcomeList.ItemsSource = update
                ? new string[0]
                : new[]
                {
                    "LOTUS, the app where you set the policy.",
                    "LOTUS Guard, the Windows service that enforces it.",
                    "A notifier next to the clock that tells you when a device is blocked."
                };
            WelcomeNote.Text = update
                ? "Your current policy stays in force while the update runs."
                : "Nothing is blocked after installation. LOTUS stays disarmed until you pick a policy and switch it on.";
            WelcomePath.Text = "Installs to " + Installer.InstallDir;
            PrimaryButton.Content = update ? "UPDATE" : "INSTALL";
            if (!Installer.HasPayload)
            {
                WelcomeNote.Text = "This copy of LOTUS-Setup was built without the program files. Build it with build.ps1.";
                PrimaryButton.IsEnabled = false;
            }
            AskForPasswordIfSet(update);
        }

        /// <summary>Updating or removing a protected LOTUS needs its password.</summary>
        private void AskForPasswordIfSet(bool update)
        {
            if (!uninstall && !update) return;
            Installer.PasswordState state = Installer.ReadPasswordState();
            if (state.Lockdown && !uninstall)
            {
                WelcomeNote.Text = "LOTUS is locked down because its policy store was damaged. This update resets it: " +
                                   "LOTUS starts disarmed again and you set a new policy and password.";
            }
            passwordNeeded = state.Needed;
            PasswordPanel.Visibility = passwordNeeded ? Visibility.Visible : Visibility.Collapsed;
            if (passwordNeeded) Loaded += (s, e) => PasswordField.Focus();
        }

        private async void OnPrimary(object sender, RoutedEventArgs e)
        {
            switch (stage)
            {
                case Stage.Welcome:
                    if (passwordNeeded && !await PasswordIsRight()) break;
                    await Work();
                    break;
                case Stage.Done when !uninstall:
                    OpenLotus();
                    Close();
                    break;
                default:
                    Close();
                    break;
            }
        }

        private void OnSecondary(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>Checks the password before any work starts, so a typing mistake changes nothing.</summary>
        private async Task<bool> PasswordIsRight()
        {
            string entered = PasswordField.Password;
            PrimaryButton.IsEnabled = false;
            string problem = await Task.Run(() => Installer.CheckPassword(entered));
            PrimaryButton.IsEnabled = true;
            if (problem == null)
            {
                PasswordError.Visibility = Visibility.Collapsed;
                return true;
            }
            PasswordError.Text = problem;
            PasswordError.Visibility = Visibility.Visible;
            PasswordField.Clear();
            PasswordField.Focus();
            return false;
        }

        private async Task Work()
        {
            stage = Stage.Working;
            WelcomePanel.Visibility = Visibility.Collapsed;
            WorkPanel.Visibility = Visibility.Visible;
            WorkTitle.Text = uninstall ? "REMOVING" : "INSTALLING";
            WorkSummary.Text = "This takes a few seconds.";
            PrimaryButton.IsEnabled = false;
            SecondaryButton.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = false;

            var installer = new Installer(Log) { Password = PasswordField.Password };
            PasswordField.Clear();
            PasswordPanel.Visibility = Visibility.Collapsed;
            string failure = await RunInBackground(() =>
            {
                fileLog(uninstall ? "=== uninstall ===" : "=== install ===");
                if (uninstall) installer.Uninstall();
                else installer.Install();
            });

            CloseButton.IsEnabled = true;
            PrimaryButton.IsEnabled = true;
            if (failure != null)
            {
                stage = Stage.Failed;
                ExitCode = 1;
                Log("FAILED: " + failure);
                WorkTitle.Text = "FAILED";
                WorkSummary.Text = failure;
                PrimaryButton.Content = "CLOSE";
                PrimaryButton.Style = (Style)FindResource("Btn.Ghost");
                return;
            }

            stage = Stage.Done;
            ExitCode = installer.Warnings.Count == 0 ? 0 : 2;
            WorkTitle.Text = "DONE";
            if (uninstall)
            {
                WorkSummary.Text = installer.Warnings.Count == 0
                    ? "LOTUS is removed. Every USB device works on this PC again."
                    : "LOTUS is removed, but some steps need your attention. See the lines marked Warning.";
                PrimaryButton.Content = "CLOSE";
                PrimaryButton.Style = (Style)FindResource("Btn.Primary");
            }
            else
            {
                WorkSummary.Text = installer.Warnings.Count == 0
                    ? "LOTUS is installed and disarmed. Open it to choose your policy."
                    : "LOTUS is installed, but some steps need your attention. See the lines marked Warning.";
                PrimaryButton.Content = "OPEN LOTUS";
                SecondaryButton.Content = "CLOSE";
                SecondaryButton.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Runs the work off the UI thread and returns the failure message, or null
        /// when it worked. The thread is a single-threaded apartment because the
        /// Windows shell object that writes shortcuts only works in one.
        /// </summary>
        internal static Task<string> RunInBackground(Action work)
        {
            var done = new TaskCompletionSource<string>();
            var thread = new System.Threading.Thread(() =>
            {
                string failure = null;
                try
                {
                    work();
                }
                catch (SetupException ex)
                {
                    failure = ex.Message;
                }
                catch (Exception ex)
                {
                    failure = "Something unexpected went wrong. " + ex.Message;
                }
                done.SetResult(failure);
            }) { IsBackground = true, Name = "LOTUS setup" };
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            return done.Task;
        }

        private void Log(string line)
        {
            fileLog(line);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                lines.Add(line);
                LogScroll.ScrollToEnd();
            }));
        }

        private static void OpenLotus()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Installer.InstallDir, Installer.AppExe))
                {
                    UseShellExecute = true
                });
            }
            catch (Exception)
            {
                // The shortcut on the desktop still works.
            }
        }

        // ---------- screenshots for the documentation ----------

        public static int RenderScreens(string directory, Application app)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Lotus.Ui.GlitchText.AnimationsEnabled = false;
            Directory.CreateDirectory(directory);

            var install = new SetupWindow(false, null) { ShowActivated = false };
            install.WelcomeTitle.Text = "INSTALL";
            install.Show();
            install.Capture(Path.Combine(directory, "setup-1-welcome.png"));

            install.stage = Stage.Done;
            install.WelcomePanel.Visibility = Visibility.Collapsed;
            install.WorkPanel.Visibility = Visibility.Visible;
            install.WorkTitle.Text = "DONE";
            install.WorkSummary.Text = "LOTUS is installed and disarmed. Open it to choose your policy.";
            foreach (string line in new[]
                     {
                         "Installing LOTUS to " + Installer.InstallDir, "Copying program files",
                         "Protecting the policy folder", "Registering the LOTUS Guard service", "Creating shortcuts",
                         "Adding LOTUS to the list of installed apps", "Starting LOTUS Guard",
                         "Done. LOTUS is installed and disarmed. Nothing is blocked until you switch it on."
                     })
            {
                install.lines.Add(line);
            }
            install.PrimaryButton.Content = "OPEN LOTUS";
            install.PrimaryButton.IsEnabled = true;
            install.SecondaryButton.Content = "CLOSE";
            install.Capture(Path.Combine(directory, "setup-2-done.png"));
            install.Close();

            var remove = new SetupWindow(true, null) { ShowActivated = false };
            remove.Show();
            remove.Capture(Path.Combine(directory, "setup-3-remove.png"));
            remove.Close();

            app.Shutdown();
            return 0;
        }

        private void Capture(string path)
        {
            Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var element = (FrameworkElement)Content;
            int width = (int)Math.Ceiling(element.ActualWidth);
            int height = (int)Math.Ceiling(element.ActualHeight);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            var visual = new System.Windows.Media.DrawingVisual();
            using (System.Windows.Media.DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x08, 0x08, 0x0C)), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(element)
                {
                    Stretch = System.Windows.Media.Stretch.None,
                    AlignmentX = System.Windows.Media.AlignmentX.Left,
                    AlignmentY = System.Windows.Media.AlignmentY.Top
                }, null, new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }

        private void OnMinimize(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            // Stopping halfway would leave a half-installed service behind.
            if (stage == Stage.Working) e.Cancel = true;
        }
    }
}
