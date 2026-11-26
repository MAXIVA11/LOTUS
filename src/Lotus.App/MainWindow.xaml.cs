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
            Closed += (s, e) => timer.Stop();
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
            RunSave(() => vm.Commit(plan, trust));
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
                RunSave(() => vm.Trust(row, TrustKind.Device));
            }
        }

        private void OnTrustModel(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DeviceRow row)
            {
                RunSave(() => vm.Trust(row, TrustKind.Model));
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
            RunSave(() => vm.Save(next));
            return true;
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
                if (backend.LogFolder != null) Process.Start("explorer.exe", "\"" + backend.LogFolder + "\"");
            }
            catch (Exception ex)
            {
                ShowMessage("COULD NOT OPEN THE FOLDER", ex.Message);
            }
        }

        private void RunSave(Action save)
        {
            try
            {
                save();
            }
            catch (Exception ex)
            {
                ShowMessage("THE POLICY WAS NOT SAVED", ex.Message);
            }
            Refresh();
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
            OverlayConfirm.Focus();
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

            OverlayConfirm.Content = request.ConfirmText;
            OverlayConfirm.Style = (Style)FindResource(request.Danger ? "Btn.Danger" : "Btn.Primary");
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
            TaskCompletionSource<bool> pending = overlayResult;
            overlayResult = null;
            pending?.TrySetResult(result);
        }
    }
}
