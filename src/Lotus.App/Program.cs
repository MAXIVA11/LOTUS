using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lotus.App.Backend;
using Lotus.Core;
using Lotus.Ui;

namespace Lotus.App
{
    public static class Program
    {
        private const string MainMutex = @"Local\LOTUS.MainWindow";
        private const string ActivateEvent = @"Local\LOTUS.Activate";
        private const string TrayMutex = @"Local\LOTUS.Tray";
        private static GuardClient devGuard;

        private static RealBackend CreateBackend()
        {
            return devGuard != null ? new RealBackend(devGuard) : new RealBackend();
        }

        [STAThread]
        public static int Main(string[] args)
        {
            var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string shotDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--shots", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    shotDir = args[++i];
                }
                else if (args[i].Equals("--dev", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    // For developers: talk to a guard started with "LotusGuard --console --dev <folder>".
                    LotusPaths.OverrideDataDir(args[++i]);
                    devGuard = new GuardClient(GuardProtocol.DevPipeName, false);
                }
                else
                {
                    options.Add(args[i]);
                }
            }

            if (shotDir != null)
            {
                return options.Contains("--real") ? RunRealScreenshots(shotDir) : RunScreenshots(shotDir);
            }
            if (options.Contains("--tray")) return RunTray();
            if (options.Contains("--demo")) return RunDemo();
            return RunMain(args);
        }

        private static Application CreateApp(ShutdownMode mode)
        {
            var app = new Application { ShutdownMode = mode };
            string assembly = Assembly.GetExecutingAssembly().GetName().Name;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/" + assembly + ";component/Theme/Theme.xaml", UriKind.Relative)
            });
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show(e.Exception.Message, "LOTUS", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            return app;
        }

        // ---------- the main window ----------

        private static int RunMain(string[] args)
        {
            if (devGuard != null)
            {
                Application devApp = CreateApp(ShutdownMode.OnMainWindowClose);
                return devApp.Run(new MainWindow(CreateBackend()));
            }
            if (!IsAdministrator())
            {
                // Changing the policy needs administrator rights. Ask Windows for them
                // and let the elevated copy take over.
                try
                {
                    Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location)
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = string.Join(" ", args)
                    });
                }
                catch (Win32Exception)
                {
                    // The person said no at the Windows prompt.
                }
                return 0;
            }

            using (var mutex = new Mutex(true, MainMutex, out bool first))
            {
                if (!first)
                {
                    // LOTUS is already open. Bring that window forward instead.
                    try
                    {
                        using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ActivateEvent)) signal.Set();
                    }
                    catch (WaitHandleCannotBeOpenedException)
                    {
                    }
                    return 0;
                }

                Application app = CreateApp(ShutdownMode.OnMainWindowClose);
                var window = new MainWindow(new RealBackend());
                using (var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEvent))
                {
                    var listener = new Thread(() =>
                    {
                        while (true)
                        {
                            try
                            {
                                activate.WaitOne();
                            }
                            catch (ObjectDisposedException)
                            {
                                return;
                            }
                            window.Dispatcher.BeginInvoke(new Action(() => BringForward(window)));
                        }
                    }) { IsBackground = true };
                    listener.Start();
                    int code = app.Run(window);
                    GC.KeepAlive(mutex);
                    return code;
                }
            }
        }

        private static void BringForward(Window window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
        }

        private static bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        // ---------- demo ----------

        private static int RunDemo()
        {
            Application app = CreateApp(ShutdownMode.OnMainWindowClose);
            var backend = new DemoBackend();
            var window = new MainWindow(backend);
            backend.Blocked += e => window.Dispatcher.BeginInvoke(new Action(() =>
                ToastWindow.Show(e, () => BringForward(window))));
            return app.Run(window);
        }

        // ---------- the notifier next to the clock ----------

        private static int RunTray()
        {
            // For testing: let the notifier read its log from another folder. The
            // notifier has no rights of its own and only reads, so this is harmless.
            string folder = Environment.GetEnvironmentVariable("LOTUS_DATA_DIR");
            bool testFolder = !string.IsNullOrWhiteSpace(folder);
            if (testFolder) LotusPaths.OverrideDataDir(folder);

            using (var mutex = new Mutex(true, TrayMutex + (testFolder ? ".Test" : ""), out bool first))
            {
                if (!first) return 0;
                Application app = CreateApp(ShutdownMode.OnExplicitShutdown);
                using (new TrayHost(OpenMainWindow))
                {
                    int code = app.Run();
                    GC.KeepAlive(mutex);
                    return code;
                }
            }
        }

        private static void OpenMainWindow()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
            }
        }

        // ---------- screenshots for the documentation ----------

        private static int RunScreenshots(string directory)
        {
            Directory.CreateDirectory(directory);
            GlitchText.AnimationsEnabled = false;
            Application app = CreateApp(ShutdownMode.OnExplicitShutdown);
            var backend = new DemoBackend();
            var window = new MainWindow(backend) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            window.Show();

            void Settle()
            {
                window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
            }

            void Shot(string name)
            {
                window.RefreshNow();
                Settle();
                Save((FrameworkElement)window.Content, Path.Combine(directory, name + ".png"));
            }

            MainViewModel vm = window.ViewModel;
            Shot("01-control-disarmed");

            // Arm it: storage blocked, other devices trusted only.
            vm.DraftArmed = true;
            vm.StorageBlock = true;
            vm.PnpBlock = true;
            Shot("02-control-pending");

            ApplyPlan plan = vm.PlanApply();
            window.ShowOverlayForScreenshot(plan.Confirm);
            Shot("03-apply-dialog");
            // Trust the keyboard and mouse models but leave the headset out, so the
            // screenshots show a blocked device.
            plan.TrustCandidates.RemoveAll(d => d.Name.Contains("HyperX"));
            window.HideOverlay();

            // Arming for the first time asks for a password.
            window.ShowOverlayForScreenshot(new ConfirmRequest
            {
                Title = "SET A PASSWORD",
                Intro = "Choose a LOTUS password before arming. Every later change to the policy asks for it, " +
                        "and so does removing LOTUS. LOTUS cannot recover it if it is lost.",
                ConfirmText = "SET AND ARM",
                Ask = PasswordAsk.Create
            });
            window.FillPasswordsForScreenshot();
            Shot("03b-password");
            window.HideOverlay();

            Policy armed = vm.Finish(plan, true);
            backend.SavePolicy(armed, "", "demo password");
            vm.Accepted(armed, true);

            backend.ToggleStick();
            window.RefreshNow();
            Shot("04-control-armed");
            window.GoTo("devices");
            Shot("05-devices");
            window.GoTo("trusted");
            Shot("06-trusted");
            window.GoTo("activity");
            Shot("07-activity");
            window.GoTo("about");
            Shot("08-about");

            var toast = new ToastWindow(new LotusEvent
            {
                Type = EventTypes.Blocked,
                DeviceName = "SanDisk Cruzer Blade USB Device",
                Message = "SanDisk Cruzer Blade USB Device was blocked because removable storage is blocked and this device is not trusted."
            }, null) { Left = 60, Top = 60 };
            toast.Show();
            window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
            Thread.Sleep(400);
            window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
            Save((FrameworkElement)toast.Content, Path.Combine(directory, "09-toast.png"));
            toast.Close();

            window.Close();
            app.Shutdown();
            return 0;
        }

        /// <summary>
        /// Developer option: draws the control and device pages from the real state of
        /// this PC. It only reads, so it needs no administrator rights.
        /// </summary>
        private static int RunRealScreenshots(string directory)
        {
            Directory.CreateDirectory(directory);
            GlitchText.AnimationsEnabled = false;
            Application app = CreateApp(ShutdownMode.OnExplicitShutdown);
            var window = new MainWindow(CreateBackend()) { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = 40, Top = 40 };
            window.Show();
            foreach (string page in new[] { "control", "devices" })
            {
                window.GoTo(page);
                window.RefreshNow();
                window.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Save((FrameworkElement)window.Content, Path.Combine(directory, "real-" + page + ".png"));
            }
            window.Close();
            app.Shutdown();
            return 0;
        }

        private static void Save(FrameworkElement element, string path)
        {
            int width = (int)Math.Ceiling(element.ActualWidth);
            int height = (int)Math.Ceiling(element.ActualHeight);
            if (width <= 0 || height <= 0) return;
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            // Draw onto a solid background, because the window itself is not part of the picture.
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0C)), null, new Rect(0, 0, width, height));
                dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, width, height));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
