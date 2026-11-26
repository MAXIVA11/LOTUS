using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Lotus.Core;
using Forms = System.Windows.Forms;

namespace Lotus.App
{
    /// <summary>
    /// The notifier that sits next to the clock. It runs as the signed-in user with
    /// no special rights, reads the activity log, and shows a pop-up whenever LOTUS
    /// Guard blocks a device.
    /// </summary>
    public sealed class TrayHost : IDisposable
    {
        private readonly Forms.NotifyIcon icon;
        private readonly ActivityTail tail = new ActivityTail(LotusPaths.EventsFile);
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Action openMain;
        private TrayMenuWindow menu;
        private int ticks;
        private string state = "";

        public TrayHost(Action openMain)
        {
            this.openMain = openMain;
            icon = new Forms.NotifyIcon { Icon = LoadIcon(), Text = "LOTUS", Visible = true };
            icon.MouseClick += OnIconClick;
            timer.Tick += (s, e) => Poll();
            UpdateState();
            timer.Start();
        }

        private static System.Drawing.Icon LoadIcon()
        {
            Uri uri = new Uri("pack://application:,,,/Assets/lotus.ico");
            using (Stream stream = Application.GetResourceStream(uri).Stream)
            {
                return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }

        private void OnIconClick(object sender, Forms.MouseEventArgs e)
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                openMain();
                return;
            }
            menu?.Close();
            menu = new TrayMenuWindow(state, openMain, () => Application.Current.Shutdown());
            menu.Closed += (s, a) => menu = null;
            menu.Show();
        }

        private void Poll()
        {
            foreach (LotusEvent e in tail.ReadNew())
            {
                if (e.Type == EventTypes.Blocked || e.Type == EventTypes.Tamper) ToastWindow.Show(e, openMain);
            }
            // The policy rarely changes, so look at it every five seconds only.
            if (++ticks % 5 == 0) UpdateState();
        }

        private void UpdateState()
        {
            // The notifier has no rights of its own and cannot read the policy. It
            // reads the small status file LOTUS Guard keeps for it.
            PublicStatus status = PublicStatus.Read();
            if (!status.Known)
            {
                state = "";
                icon.Text = "LOTUS";
                return;
            }
            state = status.Lockdown ? "LOCKED DOWN" : status.Armed ? "ARMED" : "DISARMED";
            icon.Text = "LOTUS: " + state.ToLowerInvariant();
        }

        public void Dispose()
        {
            timer.Stop();
            icon.Visible = false;
            icon.Dispose();
        }
    }
}
