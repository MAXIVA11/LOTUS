using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lotus.Core;

namespace Lotus.App
{
    /// <summary>
    /// The pop-up that tells the person at the keyboard why the thing they just
    /// plugged in does nothing.
    /// </summary>
    public partial class ToastWindow : Window
    {
        private static readonly List<ToastWindow> Open = new List<ToastWindow>();
        // Long enough to notice from across the desk, read, and walk over.
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(25);
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = Lifetime };
        private readonly Action onClick;

        public ToastWindow(LotusEvent e, Action onClick)
        {
            InitializeComponent();
            this.onClick = onClick;

            if (e.Type == EventTypes.Tamper)
            {
                // Someone interfered with LOTUS itself. Say what happened in full.
                Headline.Text = "TAMPERING";
                DeviceName.Text = "Someone tried to change LOTUS";
                ReasonText.Text = ((e.Message ?? "") + " Click to open LOTUS.").Trim();
            }
            else
            {
                string name = string.IsNullOrWhiteSpace(e.DeviceName) ? "A USB device" : e.DeviceName;
                DeviceName.Text = name;
                // The log line reads "<name> was blocked because <rule>." The pop-up already
                // shows the name and the word BLOCKED, so only the rule is left to say.
                string reason = e.Message ?? "";
                int because = reason.IndexOf(" because ", StringComparison.Ordinal);
                if (because >= 0) reason = reason.Substring(because + " because ".Length);
                if (reason.Length > 0) reason = char.ToUpperInvariant(reason[0]) + reason.Substring(1);
                ReasonText.Text = (reason + " Click to open LOTUS.").Trim();
            }

            timer.Tick += (s, a) => Close();
            // It stays for as long as the pointer rests on it, and the clock starts
            // again when the pointer leaves.
            MouseEnter += (s, a) => timer.Stop();
            MouseLeave += (s, a) => { if (IsLoaded) timer.Start(); };
            MouseLeftButtonUp += OnClicked;
            Loaded += OnLoaded;
            Closed += (s, a) =>
            {
                timer.Stop();
                Open.Remove(this);
                Restack();
            };
        }

        public static void Show(LotusEvent e, Action onClick)
        {
            // Three pop-ups are plenty. Older ones make room.
            while (Open.Count >= 3) Open[0].Close();
            var toast = new ToastWindow(e, onClick);
            Open.Add(toast);
            toast.Show();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Restack();
            timer.Start();
            var slide = new TranslateTransform(40, 0);
            Root.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }

        /// <summary>Stacks the open pop-ups upward from the bottom right corner of the work area.</summary>
        private static void Restack()
        {
            Rect area = SystemParameters.WorkArea;
            double bottom = area.Bottom - 12;
            for (int i = Open.Count - 1; i >= 0; i--)
            {
                ToastWindow toast = Open[i];
                if (!toast.IsLoaded) continue;
                toast.Left = area.Right - toast.ActualWidth - 12;
                toast.Top = bottom - toast.ActualHeight;
                bottom = toast.Top - 6;
            }
        }

        private void OnClicked(object sender, MouseButtonEventArgs e)
        {
            Close();
            onClick?.Invoke();
        }

        private void OnDismiss(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Close();
        }
    }
}
