using System;
using System.Windows;

namespace Lotus.App
{
    /// <summary>The small menu that opens from the LOTUS icon next to the clock.</summary>
    public partial class TrayMenuWindow : Window
    {
        private readonly Action open;
        private readonly Action quit;
        private bool closing;

        public TrayMenuWindow(string state, Action open, Action quit)
        {
            InitializeComponent();
            this.open = open;
            this.quit = quit;
            StateText.Text = state;
            Deactivated += (s, e) => CloseOnce();
            Loaded += (s, e) => PlaceNearCursor();
        }

        private void PlaceNearCursor()
        {
            System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
            // Screen pixels to WPF units.
            PresentationSource source = PresentationSource.FromVisual(this);
            double scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
            double x = cursor.X * scaleX;
            double y = cursor.Y * scaleY;

            Rect area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left + 4, Math.Min(x - ActualWidth + 12, area.Right - ActualWidth - 4));
            Top = Math.Max(area.Top + 4, Math.Min(y - ActualHeight - 8, area.Bottom - ActualHeight - 4));
            Activate();
        }

        private void CloseOnce()
        {
            if (closing) return;
            closing = true;
            Close();
        }

        private void OnOpen(object sender, RoutedEventArgs e)
        {
            CloseOnce();
            open?.Invoke();
        }

        private void OnQuit(object sender, RoutedEventArgs e)
        {
            CloseOnce();
            quit?.Invoke();
        }
    }
}
