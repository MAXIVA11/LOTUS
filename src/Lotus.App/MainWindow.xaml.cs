using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Lotus.App.Backend;
using Lotus.Core;

namespace Lotus.App
{
    public partial class MainWindow : Window
    {
        private readonly IAppBackend backend;
        private readonly MainViewModel vm;
        private readonly DispatcherTimer timer;
        private bool reading;
        private TaskCompletionSource<bool> overlayResult;
        private PasswordAsk overlayAsk = PasswordAsk.None;
        // The password is kept for a few minutes after it was last used, so that
        // trusting three devices in a row does not ask three times.
        private static readonly TimeSpan PasswordMemory = TimeSpan.FromMinutes(5);
        private string sessionPassword;
        private DateTime sessionPasswordUsed = DateTime.MinValue;

        public MainWindow(IAppBackend backend)
        {
            this.backend = backend;
            vm = new MainViewModel(backend);
            DataContext = vm;
            InitializeComponent();

            OpenLogButton.Visibility = backend.LogFolder == null ? Visibility.Collapsed : Visibility.Visible;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) =>
            {
                Refresh();
                timer.Start();
            };
            Closed += (s, e) =>
            {
                timer.Stop();
                sessionPassword = null;
            };
            StateChanged += (s, e) => OnWindowStateChanged();
            PreviewKeyDown += OnPreviewKeyDown;
        }

        public MainViewModel ViewModel => vm;

        // ---------- data ----------

        /// <summary>Reads the current state off the UI thread and shows it.</summary>
        public async void Refresh()
        {
            if (reading) return;
            reading = true;
            try
            {
                AppSnapshot snapshot = await Task.Run(() => backend.Read());
                vm.ApplySnapshot(snapshot);
                UpdateDemoButton();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Refresh failed: " + ex);
            }
            finally
            {
                reading = false;
            }
        }

        /// <summary>Reads the state right now, on the calling thread. Used for screenshots.</summary>
        public void RefreshNow()
        {
            vm.ApplySnapshot(backend.Read());
            UpdateDemoButton();
        }

        private void UpdateDemoButton()
        {
            if (backend is DemoBackend demo)
            {
                DemoPlug.Content = demo.StickPlugged ? "UNPLUG THE FLASH DRIVE" : "PLUG IN A FLASH DRIVE";
            }
        }

        // ---------- window chrome ----------

        private void OnMinimize(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximize(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnWindowStateChanged()
        {
            // A maximized borderless window hangs over the screen edge by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Overlay.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Escape)
            {
                CloseOverlay(false);
                e.Handled = true;
            }
        }

        // ---------- navigation ----------

        private void OnNavigate(object sender, RoutedEventArgs e)
        {
            if (PageControl == null || PageAbout == null) return; // still loading
            Show(PageControl, sender == NavControl);
            Show(PageDevices, sender == NavDevices);
            Show(PageTrusted, sender == NavTrusted);
            Show(PageActivity, sender == NavActivity);
            Show(PageAbout, sender == NavAbout);
        }

        private static void Show(UIElement page, bool visible)
        {
            page.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Switches to a page by name: control, devices, trusted, activity or about.</summary>
        public void GoTo(string page)
        {
            switch (page)
            {
                case "devices": NavDevices.IsChecked = true; break;
                case "trusted": NavTrusted.IsChecked = true; break;
                case "activity": NavActivity.IsChecked = true; break;
                case "about": NavAbout.IsChecked = true; break;
                default: NavControl.IsChecked = true; break;
            }
        }

        // ---------- control page ----------

        private void OnDiscard(object sender, RoutedEventArgs e)
        {
            vm.Discard();
        }

        private async void OnApply(object sender, RoutedEventArgs e)
        {
            ApplyPlan plan = vm.PlanApply();
            bool confirmed = await Confirm(plan.Confirm);
            if (!confirmed) return;
            bool trust = plan.TrustCandidates.Count > 0 && OverlayCheck.IsChecked == true;
            await Save(vm.Finish(plan, trust), true);
        }

        private async void OnChangePassword(object sender, RoutedEventArgs e)
        {
            bool change = vm.HasPassword;
            string error = "";
            while (true)
            {
                PasswordAnswer answer = await AskPassword(new ConfirmRequest
                {
                    Title = change ? "CHANGE PASSWORD" : "SET A PASSWORD",
                    Intro = change
                        ? "Enter the current LOTUS password and choose a new one."
                        : "Choose a LOTUS password. Every later change to the policy asks for it, and LOTUS cannot recover it if it is lost.",
                    ConfirmText = change ? "CHANGE" : "SET",
                    Ask = change ? PasswordAsk.Change : PasswordAsk.Create,
                    Error = error
                });
                if (answer == null) return;

                SaveResult result = await Task.Run(() => backend.ChangePassword(answer.Current, answer.New));
                if (result.Ok)
                {
                    Remember(answer.New);
                    Refresh();
                    return;
                }
                error = result.Message;
                if (result.Code != GuardProtocol.ErrWrongPassword && result.Code != GuardProtocol.ErrWeakPassword &&
                    result.Code != GuardProtocol.ErrPasswordNeeded && result.Code != GuardProtocol.ErrLockedOut)
                {
                    ShowMessage("THE PASSWORD WAS NOT CHANGED", result.Message);
                    return;
                }
            }
        }

        private void OnStartGuard(object sender, RoutedEventArgs e)
        {
            string error = backend.StartGuard();
            if (error != null)
            {
                ShowMessage("GUARD DID NOT START", error);
            }
            Refresh();
        }

        // ---------- devices and trust ----------

        private void OnTrustDevice(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DeviceRow row)
            {
                _ = Save(vm.WithTrust(row, TrustKind.Device), false);
            }
        }

        private void OnTrustModel(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DeviceRow row)
            {
                _ = Save(vm.WithTrust(row, TrustKind.Model), false);
            }
        }

        private async void OnUntrustDevice(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is DeviceRow row)) return;
            // A device can be covered by a device entry and a model entry at once.
            foreach (TrustEntry entry in vm.TrustEntriesFor(row))
            {
                if (!await RemoveTrust(entry.Kind, entry.Id)) return;
            }
        }

        private async void OnRemoveTrust(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is TrustRow row)
            {
                await RemoveTrust(row.Kind, row.Id);
            }
        }

        private async Task<bool> RemoveTrust(TrustKind kind, string id)
        {
            ConfirmRequest confirm = vm.PlanUntrust(kind, id, out Policy next);
            if (confirm != null && !await Confirm(confirm)) return false;
            return await Save(next, false);
        }

        private void OnDemoPlug(object sender, RoutedEventArgs e)
        {
            if (backend is DemoBackend demo)
            {
                demo.ToggleStick();
                Refresh();
            }
        }

        private void OnOpenLogFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                if (backend.LogFolder != null)
                {
                    // The full path, so nothing else called explorer.exe can be picked up.
                    string explorer = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    Process.Start(explorer, "\"" + backend.LogFolder + "\"");
                }
            }
            catch (Exception ex)
            {
                ShowMessage("COULD NOT OPEN THE FOLDER", ex.Message);
            }
        }

        private void Remember(string password)
        {
            sessionPassword = password;
            sessionPasswordUsed = DateTime.UtcNow;
        }

        /// <summary>
        /// Sends a policy to LOTUS Guard. The guard decides whether a password is
        /// needed, and this asks for it as often as it takes. Returns true once the
        /// policy is in force.
        /// </summary>
        private async Task<bool> Save(Policy next, bool followSettings)
        {
            if (next == null) return false;
            if (sessionPassword != null && DateTime.UtcNow - sessionPasswordUsed > PasswordMemory)
            {
                sessionPassword = null;
            }
            string password = sessionPassword ?? "";
            string newPassword = null;

            for (int round = 0; round < 12; round++)
            {
                SaveResult result;
                try
                {
                    string p = password, n = newPassword;
                    result = await Task.Run(() => backend.SavePolicy(next, p, n));
                }
                catch (Exception ex)
                {
                    ShowMessage("THE POLICY WAS NOT SAVED", ex.Message);
                    return false;
                }

                if (result.Ok)
                {
                    if (newPassword != null) Remember(newPassword);
                    else if (password.Length > 0) Remember(password);
                    vm.Accepted(next, followSettings);
                    Refresh();
                    return true;
                }

                switch (result.Code)
                {
                    case GuardProtocol.ErrPasswordNeeded:
                    case GuardProtocol.ErrWrongPassword:
                    case GuardProtocol.ErrLockedOut:
                    {
                        sessionPassword = null;
                        PasswordAnswer answer = await AskPassword(new ConfirmRequest
                        {
                            Title = "LOTUS PASSWORD",
                            Intro = "This change needs the LOTUS password.",
                            ConfirmText = "CONTINUE",
                            Ask = PasswordAsk.Enter,
                            Error = result.Code == GuardProtocol.ErrPasswordNeeded ? "" : result.Message
                        });
                        if (answer == null) return false;
                        password = answer.Current;
                        break;
                    }
                    case GuardProtocol.ErrNewPasswordNeeded:
                    case GuardProtocol.ErrWeakPassword:
                    {
                        PasswordAnswer answer = await AskPassword(new ConfirmRequest
                        {
                            Title = "SET A PASSWORD",
                            Intro = "Choose a LOTUS password before arming. Every later change to the policy asks " +
                                    "for it, and so does removing LOTUS. LOTUS cannot recover it if it is lost.",
                            ConfirmText = "SET AND ARM",
                            Ask = PasswordAsk.Create,
                            Error = result.Code == GuardProtocol.ErrWeakPassword ? result.Message : ""
                        });
                        if (answer == null) return false;
                        newPassword = answer.New;
                        break;
                    }
                    default:
                        ShowMessage("THE POLICY WAS NOT SAVED", result.Message);
                        Refresh();
                        return false;
                }
            }
            return false;
        }

        /// <summary>Shows a dialog with password fields. Returns null when it was cancelled.</summary>
        private async Task<PasswordAnswer> AskPassword(ConfirmRequest request)
        {
            if (!await Confirm(request)) return null;
            var answer = new PasswordAnswer { Current = OverlayPwCurrent.Password, New = OverlayPwNew.Password };
            ClearPasswordBoxes();
            return answer;
        }

        private void ClearPasswordBoxes()
        {
            OverlayPwCurrent.Clear();
            OverlayPwNew.Clear();
            OverlayPwRepeat.Clear();
        }

        // ---------- confirmation overlay ----------

        public Task<bool> Confirm(ConfirmRequest request)
        {
            FillOverlay(request);
            OpenOverlay();
            return overlayResult.Task;
        }

        private void OpenOverlay()
        {
            // An earlier dialog that is still open counts as cancelled.
            overlayResult?.TrySetResult(false);
            overlayResult = new TaskCompletionSource<bool>();
            Body.IsEnabled = false; // keeps the keyboard out of the page behind the dialog
            Overlay.Visibility = Visibility.Visible;
            if (overlayAsk == PasswordAsk.Enter || overlayAsk == PasswordAsk.Change) OverlayPwCurrent.Focus();
            else if (overlayAsk == PasswordAsk.Create) OverlayPwNew.Focus();
            else OverlayConfirm.Focus();
        }

        /// <summary>Fills in the dialog without waiting for an answer. Used for screenshots.</summary>
        public void FillOverlay(ConfirmRequest request)
        {
            OverlayTitle.Text = request.Title;
            OverlayIntro.Text = request.Intro;
            OverlayIntro.Visibility = Visible(request.Intro.Length > 0);

            OverlayOff.Visibility = Visible(request.Off.Count > 0);
            OverlayOffCaption.Text = request.OffCaption;
            OverlayOffList.ItemsSource = Shorten(request.Off);

            OverlayUntrusted.Visibility = Visible(request.Untrusted.Count > 0);
            OverlayUntrustedCaption.Text = request.UntrustedCaption;
            OverlayUntrustedList.ItemsSource = Shorten(request.Untrusted);
            OverlayCheck.Visibility = Visible(request.CheckText.Length > 0);
            OverlayCheckText.Text = request.CheckText;
            OverlayCheck.IsChecked = request.Checked;

            OverlayOn.Visibility = Visible(request.On.Count > 0);
            OverlayOnCaption.Text = request.OnCaption;
            OverlayOnList.ItemsSource = Shorten(request.On);

            OverlayWarning.Visibility = Visible(request.Warning.Length > 0);
            OverlayWarningText.Text = request.Warning;

            overlayAsk = request.Ask;
            ClearPasswordBoxes();
            OverlayPasswords.Visibility = Visible(request.Ask != PasswordAsk.None);
            OverlayPwCurrentRow.Visibility = Visible(request.Ask == PasswordAsk.Enter || request.Ask == PasswordAsk.Change);
            OverlayPwNewRow.Visibility = Visible(request.Ask == PasswordAsk.Create || request.Ask == PasswordAsk.Change);
            OverlayPwRepeatRow.Visibility = OverlayPwNewRow.Visibility;
            OverlayPwCurrentLabel.Text = request.Ask == PasswordAsk.Change ? "CURRENT PASSWORD" : "PASSWORD";
            OverlayPwError.Text = request.Error ?? "";
            OverlayPwError.Visibility = Visible(!string.IsNullOrEmpty(request.Error));

            OverlayConfirm.Content = request.ConfirmText;
            OverlayConfirm.Style = (Style)FindResource(request.Danger ? "Btn.Danger" : "Btn.Primary");
            OverlayConfirm.IsDefault = request.Ask != PasswordAsk.None;
            OverlayCancel.Visibility = Visible(request.ConfirmText != "OK");
        }

        public void ShowOverlayForScreenshot(ConfirmRequest request)
        {
            FillOverlay(request);
            Overlay.Visibility = Visibility.Visible;
        }

        public void HideOverlay()
        {
            CloseOverlay(false);
        }

        public void FillPasswordsForScreenshot()
        {
            OverlayPwNew.Password = "demo password";
            OverlayPwRepeat.Password = "demo password";
        }

        private static Visibility Visible(bool on)
        {
            return on ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Keeps a long device list from pushing the buttons off the window.</summary>
        private static List<string> Shorten(List<string> names)
        {
            const int max = 6;
            if (names.Count <= max) return names;
            List<string> shown = names.Take(max).ToList();
            shown.Add("and " + (names.Count - max) + " more");
            return shown;
        }

        private void ShowMessage(string title, string message)
        {
            FillOverlay(new ConfirmRequest { Title = title, Intro = message, ConfirmText = "OK" });
            OpenOverlay();
        }

        private void OnOverlayConfirm(object sender, RoutedEventArgs e)
        {
            // A new password is checked here, so a typing mistake never leaves the window.
            if (overlayAsk == PasswordAsk.Create || overlayAsk == PasswordAsk.Change)
            {
                string problem = PasswordRecord.Problem(OverlayPwNew.Password);
                if (problem == null && OverlayPwNew.Password != OverlayPwRepeat.Password)
                {
                    problem = "The two passwords are not the same.";
                }
                if (problem != null)
                {
                    OverlayPwError.Text = problem;
                    OverlayPwError.Visibility = Visibility.Visible;
                    return;
                }
            }
            if ((overlayAsk == PasswordAsk.Enter || overlayAsk == PasswordAsk.Change) &&
                OverlayPwCurrent.Password.Length == 0)
            {
                OverlayPwError.Text = "Enter the LOTUS password.";
                OverlayPwError.Visibility = Visibility.Visible;
                return;
            }
            CloseOverlay(true);
        }

        private void OnOverlayCancel(object sender, RoutedEventArgs e)
        {
            CloseOverlay(false);
        }

        private void CloseOverlay(bool result)
        {
            Overlay.Visibility = Visibility.Collapsed;
            Body.IsEnabled = true;
            if (!result) ClearPasswordBoxes();
            TaskCompletionSource<bool> pending = overlayResult;
            overlayResult = null;
            pending?.TrySetResult(result);
        }
    }
}
