using System;
using System.Collections.Generic;
using System.Linq;
using Lotus.Core;

namespace Lotus.Linux.Tui
{
    /// <summary>What the interface needs from the daemon. The tests put a pretend one in.</summary>
    internal interface ILotusSession
    {
        /// <summary>The policy, the ledger and the connected devices. Throws GuardUnavailableException when the daemon does not answer.</summary>
        Program.State Read();
        GuardReply Apply(Policy next);
        List<LotusEvent> Log(int count);
    }

    internal enum TuiPage
    {
        Control,
        Devices,
        Trusted,
        Activity,
        About
    }

    /// <summary>
    /// The interactive interface of LOTUS on Linux: the same five pages as the
    /// Windows app, drawn with characters and driven with the keyboard. This
    /// class holds what is on screen and what a key does. It knows nothing about
    /// the terminal, so the tests can press keys and read the screen.
    /// </summary>
    internal sealed class TuiApp
    {
        public const int MinWidth = 80;
        public const int MinHeight = 24;

        private static readonly string[] PageNames = { "CONTROL", "DEVICES", "TRUSTED", "ACTIVITY", "ABOUT" };

        private readonly ILotusSession session;
        private readonly string version;
        private readonly bool unicode;

        private Program.State live;
        private string error;
        private Policy draft = new Policy();
        private bool edited;
        private List<LotusEvent> log = new List<LotusEvent>();

        private int focus;
        private int deviceIndex;
        private int trustIndex;
        private int logScroll;
        private int aboutScroll;
        private Dialog dialog;
        private string toast;
        private int toastColor = Palette.Cyan;

        public TuiPage Page { get; private set; }
        public bool Quit { get; private set; }
        /// <summary>Work that takes a moment, such as sending a policy. The caller draws once, then runs it.</summary>
        public Action Deferred { get; private set; }
        public string Busy { get; private set; }

        public TuiApp(ILotusSession session, string version, bool unicode = true)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.version = version ?? "";
            this.unicode = unicode;
        }

        // ================= state =================

        /// <summary>Asks the daemon how things stand. Settings the person is in the middle of changing are kept.</summary>
        public void Refresh()
        {
            try
            {
                live = session.Read();
                error = null;
                if (!edited) CopySettings(live.Policy, draft);
                else if (draft.SettingsEqual(live.Policy)) edited = false;
            }
            catch (GuardUnavailableException ex)
            {
                live = null;
                error = ex.Message;
            }
            catch (FormatException ex)
            {
                live = null;
                error = "The LOTUS daemon sent something that could not be read. " + ex.Message;
            }
            try
            {
                log = session.Log(300);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                log = new List<LotusEvent>();
            }
            int devices = live == null ? 0 : live.Devices.Count;
            deviceIndex = Math.Max(0, Math.Min(deviceIndex, devices - 1));
            int trusted = live == null ? 0 : live.Policy.Trusted.Count;
            trustIndex = Math.Max(0, Math.Min(trustIndex, trusted - 1));
        }

        public void RunDeferred()
        {
            Action work = Deferred;
            Deferred = null;
            try
            {
                work?.Invoke();
            }
            finally
            {
                Busy = null;
            }
        }

        private static void CopySettings(Policy from, Policy to)
        {
            to.Armed = from.Armed;
            to.Storage = from.Storage;
            to.Pnp = from.Pnp;
            to.AllowInput = from.AllowInput;
            to.ApproveInput = from.ApproveInput;
            to.Strict = from.Strict;
        }

        private bool HasChanges => live != null && !draft.SettingsEqual(live.Policy);
        private bool StrictOptionEnabled => draft.Storage == StorageMode.Block || draft.Pnp == PnpMode.Block ||
                                            draft.ApproveInput;

        private void Say(string text, int color = Palette.Cyan)
        {
            toast = text;
            toastColor = color;
        }

        private void Message(string title, string text)
        {
            dialog = new MessageDialog { Title = title, Text = text };
        }

        /// <summary>Sends a policy to the daemon, after the screen has shown that it is being sent.</summary>
        private void Send(Policy next, string done, bool followSettings)
        {
            Busy = "APPLYING";
            Deferred = () =>
            {
                try
                {
                    GuardReply reply = session.Apply(next);
                    if (!reply.Ok)
                    {
                        Message("THE POLICY WAS NOT SAVED",
                            reply.Message.Length > 0 ? reply.Message : "The LOTUS daemon refused the change.");
                        return;
                    }
                    if (followSettings)
                    {
                        CopySettings(next, draft);
                        edited = false;
                    }
                    Say(done);
                    Refresh();
                }
                catch (GuardUnavailableException ex)
                {
                    Message("THE POLICY WAS NOT SAVED", ex.Message);
                }
            };
        }

        // ================= keys =================

        public void Key(ConsoleKeyInfo key)
        {
            if (Busy != null) return;
            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                Quit = true;
                return;
            }
            if (dialog != null)
            {
                Dialog open = dialog;
                if (open.Key(key)) dialog = dialog == open ? null : dialog;
                return;
            }
            toast = null;

            char c = char.ToLowerInvariant(key.KeyChar);
            if (key.Key == ConsoleKey.Escape || c == 'q')
            {
                if (HasChanges && key.Key == ConsoleKey.Escape)
                {
                    // Escape first puts the settings back the way the daemon has them.
                    CopySettings(live.Policy, draft);
                    edited = false;
                    Say("Your changes were dropped.", Palette.Yellow);
                    return;
                }
                Quit = true;
                return;
            }
            if (c >= '1' && c <= '5')
            {
                Page = (TuiPage)(c - '1');
                return;
            }
            if (key.Key == ConsoleKey.Tab)
            {
                int step = (key.Modifiers & ConsoleModifiers.Shift) != 0 ? PageNames.Length - 1 : 1;
                Page = (TuiPage)(((int)Page + step) % PageNames.Length);
                return;
            }
            if (live == null) return;

            switch (Page)
            {
                case TuiPage.Control: ControlKey(key, c); break;
                case TuiPage.Devices: DevicesKey(key, c); break;
                case TuiPage.Trusted: TrustedKey(key, c); break;
                case TuiPage.Activity: ScrollKey(key, ref logScroll, Math.Max(0, log.Count - 1)); break;
                case TuiPage.About: ScrollKey(key, ref aboutScroll, 40); break;
            }
        }

        private static void ScrollKey(ConsoleKeyInfo key, ref int scroll, int max)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: scroll--; break;
                case ConsoleKey.DownArrow: scroll++; break;
                case ConsoleKey.PageUp: scroll -= 10; break;
                case ConsoleKey.PageDown: scroll += 10; break;
                case ConsoleKey.Home: scroll = 0; break;
                case ConsoleKey.End: scroll = max; break;
            }
            scroll = Math.Max(0, Math.Min(scroll, max));
        }

        // ---------- control ----------

        private const int FocusSwitch = 0, FocusStorage = 1, FocusPnp = 2, FocusInput = 3, FocusStrict = 4, FocusApply = 5;

        private bool FocusUsable(int item)
        {
            if (item == FocusStrict) return StrictOptionEnabled;
            return true;
        }

        private void MoveFocus(int step)
        {
            for (int i = 0; i < 6; i++)
            {
                focus = (focus + step + 6) % 6;
                if (FocusUsable(focus)) return;
            }
        }

        private void ControlKey(ConsoleKeyInfo key, char c)
        {
            if (c == 'a')
            {
                StartApply();
                return;
            }
            if (live.Lockdown) return;
            bool act = key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Spacebar;
            int side = key.Key == ConsoleKey.LeftArrow ? -1 : key.Key == ConsoleKey.RightArrow ? 1 : 0;
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: MoveFocus(-1); return;
                case ConsoleKey.DownArrow: MoveFocus(1); return;
            }
            switch (focus)
            {
                case FocusSwitch:
                    if (act || side != 0) draft.Armed = side != 0 ? side > 0 : !draft.Armed;
                    break;
                case FocusStorage:
                    if (act) side = 1;
                    if (side != 0) draft.Storage = (StorageMode)(((int)draft.Storage + side + 3) % 3);
                    break;
                case FocusPnp:
                    if (act) side = 1;
                    if (side != 0) draft.Pnp = draft.Pnp == PnpMode.Allow ? PnpMode.Block : PnpMode.Allow;
                    break;
                case FocusInput:
                    if (act) side = 1;
                    if (side != 0) draft.Input = (InputRule)(((int)draft.Input + side + 3) % 3);
                    break;
                case FocusStrict:
                    if (act || side != 0) draft.Strict = !draft.Strict;
                    break;
                case FocusApply:
                    if (act) StartApply();
                    break;
            }
            edited = HasChanges;
            if (!FocusUsable(focus)) MoveFocus(1);
        }

        /// <summary>Works out what the new settings would do to the devices connected right now, and asks.</summary>
        private void StartApply()
        {
            if (live.Lockdown)
            {
                Message("LOCKED DOWN", "The policy store was damaged, so LOTUS blocks storage and untrusted devices " +
                                       "and takes no changes. Run the LOTUS installer again to reset it.");
                return;
            }
            if (!HasChanges)
            {
                Say("Nothing has changed, so there is nothing to apply.", Palette.Dim);
                return;
            }
            Policy next = live.Policy.Clone();
            CopySettings(draft, next);

            var confirm = new ConfirmDialog { Title = "APPLY POLICY", ConfirmText = "APPLY", Unicode = unicode };
            if (!next.Armed)
            {
                confirm.Intro = live.Policy.Armed
                    ? "LOTUS will be disarmed. Nothing is blocked while it is off, and every device it blocked is switched back on. Your settings are kept."
                    : "Your settings are saved, but LOTUS stays off, so nothing is blocked.";
            }
            else
            {
                string storage = next.Storage == StorageMode.Block ? "blocked"
                    : next.Storage == StorageMode.ReadOnly ? "read-only" : "allowed";
                confirm.Intro = "LOTUS will be armed.  Removable storage: " + storage + ".  Other USB devices: " +
                                (next.Pnp == PnpMode.Block ? "trusted only" : "allowed") + "." +
                                (next.ApproveInput ? "  New keyboards and mice wait for approval." : "");
            }

            var candidates = new List<UsbDevice>();
            var off = new List<UsbDevice>();
            var untrusted = new List<string>();
            var storageOff = new List<string>();
            var on = new List<string>();
            foreach (DeviceStatus status in live.Devices)
            {
                UsbDevice device = status.Device;
                if (status.State == DeviceState.OffElsewhere) continue;
                Decision decision = PolicyEngine.Evaluate(next, device, TimeSpan.MaxValue);
                if (decision.IsBlock && device.Started)
                {
                    off.Add(device);
                    if (decision.Reason == Reason.PnpBlocked || decision.Reason == Reason.InputWaiting)
                    {
                        candidates.Add(device);
                        untrusted.Add(device.Name);
                    }
                    else
                    {
                        storageOff.Add(device.Name);
                    }
                }
                else if (!decision.IsBlock && (status.State == DeviceState.Blocked || status.State == DeviceState.Blocking))
                {
                    on.Add(device.Name);
                }
            }
            // What is plugged into a hub goes down with the hub.
            var behind = new List<string>();
            foreach (UsbDevice hub in off.Where(d => d.IsHub).ToList())
            {
                foreach (DeviceStatus status in live.Devices)
                {
                    UsbDevice device = status.Device;
                    if (device.Started && !off.Contains(device) &&
                        device.Location.StartsWith(hub.Location + ".", StringComparison.Ordinal))
                    {
                        behind.Add(device.Name);
                    }
                }
            }

            confirm.AddSection("Removable storage that is switched off right away:", storageOff);
            confirm.AddSection("Connected USB devices that are not trusted yet:", untrusted);
            confirm.AddSection("Plugged into a hub that is switched off, so they go with it:", behind);
            confirm.AddSection("Switched back on:", on);
            if (candidates.Count > 0)
            {
                confirm.CheckText = candidates.Count == 1
                    ? "Trust this device, so it keeps working"
                    : "Trust these " + candidates.Count + " devices, so they keep working";
                confirm.Checked = true;
            }
            if (next.Armed && next.Pnp == PnpMode.Block && next.Input == InputRule.Trusted)
            {
                confirm.Warning = "Keyboards and mice are not exempt. One that is not trusted stops working the moment you apply.";
            }
            else if (next.Armed && next.ApproveInput && !live.Policy.ApproveInput)
            {
                confirm.Warning = "A keyboard or mouse that is not on the trusted list stops working when you apply, and waits for your approval.";
            }
            else if (next.Armed && next.Storage == StorageMode.ReadOnly && live.Policy.Storage != StorageMode.ReadOnly)
            {
                confirm.Warning = "Connected drives are reconnected for a moment so the change reaches them. Close files on them first.";
            }
            confirm.Danger = off.Count > 0 || behind.Count > 0;
            confirm.OnConfirm = trust =>
            {
                if (trust) next.TrustAll(candidates);
                Send(next, next.Armed ? "Applied. LOTUS is armed." : "Applied. LOTUS is disarmed.", true);
            };
            dialog = confirm;
        }

        // ---------- devices ----------

        private void DevicesKey(ConsoleKeyInfo key, char c)
        {
            ScrollKey(key, ref deviceIndex, Math.Max(0, live.Devices.Count - 1));
            if (live.Devices.Count == 0) return;
            UsbDevice device = live.Devices[deviceIndex].Device;
            switch (c)
            {
                case 't':
                    AddTrust(TrustEntry.ForDevice(device), device.Name + " is trusted.");
                    if (dialog == null && !device.HasSerial && toast != null)
                    {
                        toast += " It has no serial number, so this holds for the port it is in now.";
                    }
                    break;
                case 'm':
                    AddTrust(TrustEntry.ForModel(device), "Every " + device.Name + " (" + device.ModelId + ") is trusted.");
                    break;
                case 'u':
                    RemoveTrust(live.Policy.Trusted.Where(e => e.Matches(device)).ToList(),
                        device.Name + " is not on the trusted list.");
                    break;
            }
        }

        private void AddTrust(TrustEntry entry, string done)
        {
            if (live.Lockdown)
            {
                Message("LOCKED DOWN", "LOTUS takes no changes while it is locked down. Run the LOTUS installer again to reset it.");
                return;
            }
            Policy next = live.Policy.Clone();
            if (!next.AddTrust(entry))
            {
                Say("That is on the trusted list already.", Palette.Dim);
                return;
            }
            Send(next, done, false);
        }

        /// <summary>Takes entries off the trusted list. Asks first when that switches off something that works.</summary>
        private void RemoveTrust(List<TrustEntry> entries, string nothing)
        {
            if (entries.Count == 0)
            {
                Say(nothing, Palette.Dim);
                return;
            }
            if (live.Lockdown)
            {
                Message("LOCKED DOWN", "LOTUS takes no changes while it is locked down. Run the LOTUS installer again to reset it.");
                return;
            }
            Policy next = live.Policy.Clone();
            foreach (TrustEntry entry in entries) next.RemoveTrust(entry.Kind, entry.Id);

            var off = new List<string>();
            foreach (DeviceStatus status in live.Devices)
            {
                if (!status.Device.Started) continue;
                if (PolicyEngine.Evaluate(next, status.Device, TimeSpan.MaxValue).IsBlock) off.Add(status.Device.Name);
            }
            if (off.Count == 0)
            {
                Send(next, "Removed from the trusted list.", false);
                return;
            }
            var confirm = new ConfirmDialog
            {
                Title = "REMOVE TRUST", ConfirmText = "REMOVE", Danger = true, Unicode = unicode,
                Intro = "The policy blocks what is no longer trusted."
            };
            confirm.AddSection("Switched off right away:", off);
            confirm.OnConfirm = _ => Send(next, "Removed from the trusted list.", false);
            dialog = confirm;
        }

        // ---------- trusted ----------

        private void TrustedKey(ConsoleKeyInfo key, char c)
        {
            ScrollKey(key, ref trustIndex, Math.Max(0, live.Policy.Trusted.Count - 1));
            if (c == 'n' || c == 'a' || c == '+')
            {
                dialog = new AddTrustDialog(this) { Unicode = unicode };
                return;
            }
            if ((c == 'x' || c == 'd' || key.Key == ConsoleKey.Delete) && live.Policy.Trusted.Count > 0)
            {
                RemoveTrust(new List<TrustEntry> { live.Policy.Trusted[trustIndex] }, "");
                return;
            }
            if (c == 'e' && live.Policy.Trusted.Count > 0)
            {
                dialog = new TermDialog(this, live.Policy.Trusted[trustIndex]) { Unicode = unicode };
            }
        }

        /// <summary>Called by the "trust until" form. Returns a problem to show, or null when the change is on its way.</summary>
        internal string SetTerm(TrustEntry entry, string typed)
        {
            if (!TrustEntry.TryParseTerm(typed, DateTime.UtcNow, out DateTime? until, out string problem)) return problem;
            if (live.Lockdown) return "LOTUS takes no changes while it is locked down.";
            Policy next = live.Policy.Clone();
            TrustEntry target = next.Trusted.FirstOrDefault(e => e.Kind == entry.Kind && e.Id == entry.Id);
            if (target == null) return "That entry is not on the trusted list any more.";
            target.UntilUtc = until;
            string who = string.IsNullOrWhiteSpace(target.Name) ? target.Id : target.Name;
            Send(next, until.HasValue
                ? "The trust for " + who + " ends on " + target.UntilText() + "."
                : "The trust for " + who + " has no end.", false);
            return null;
        }

        /// <summary>Called by the "add by hand" form. Returns a problem to show, or null when the entry is on its way.</summary>
        internal string AddTyped(TrustKind kind, string id, string name)
        {
            if (!TrustEntry.TryParse(kind, id, name, out TrustEntry entry, out string problem)) return problem;
            if (kind == TrustKind.Device)
            {
                // A device that is plugged in is known, so what it does is recorded with the entry.
                UsbDevice seen = live.Devices.Select(d => d.Device).FirstOrDefault(d => d.InstanceId == entry.Id);
                if (seen != null)
                {
                    TrustEntry recorded = TrustEntry.ForDevice(seen);
                    if (!string.IsNullOrWhiteSpace(name)) recorded.Name = entry.Name;
                    entry = recorded;
                }
            }
            Policy next = live.Policy.Clone();
            if (!next.AddTrust(entry)) return "That is on the trusted list already.";
            if (live.Lockdown) return "LOTUS takes no changes while it is locked down.";
            Send(next, kind == TrustKind.Vendor ? "Every device from vendor " + entry.Id.Substring(4) + " is trusted."
                : kind == TrustKind.Model ? "Every " + entry.Id + " is trusted."
                : entry.Id + " is trusted.", false);
            return null;
        }

        // ================= drawing =================

        private string G(string fancy, string plain)
        {
            return unicode ? fancy : plain;
        }

        public void Render(Canvas c)
        {
            if (c.Width < MinWidth || c.Height < MinHeight)
            {
                c.TextCentered(0, c.Width, c.Height / 2 - 1, "LOTUS", Palette.Cyan, -1, true);
                c.TextCentered(0, c.Width, c.Height / 2, "Make this window larger.", Palette.Text);
                c.TextCentered(0, c.Width, c.Height / 2 + 1, "It needs " + MinWidth + " by " + MinHeight + ".", Palette.Dim);
                return;
            }
            DrawFrame(c);
            int top = 4, bottom = c.Height - 3;
            if (live == null)
            {
                DrawUnavailable(c, top);
            }
            else
            {
                switch (Page)
                {
                    case TuiPage.Control: DrawControl(c, top, bottom); break;
                    case TuiPage.Devices: DrawDevices(c, top, bottom); break;
                    case TuiPage.Trusted: DrawTrusted(c, top, bottom); break;
                    case TuiPage.Activity: DrawActivity(c, top, bottom); break;
                    case TuiPage.About: DrawAbout(c, top, bottom); break;
                }
            }
            dialog?.Render(c);
            if (Busy != null)
            {
                int width = 30, x = (c.Width - width) / 2, y = c.Height / 2 - 1;
                c.Box(x, y, width, 3, Palette.Cyan, Palette.Panel, unicode);
                c.TextCentered(x, width, y + 1, Busy + " ...", Palette.Cyan, -1, true);
            }
        }

        private void DrawFrame(Canvas c)
        {
            int w = c.Width;
            string stripe = G("▞", "/");
            // Warning stripes on two corners, like on the window.
            c.Text(0, 0, new string(stripe[0], Math.Min(28, w)), Palette.Yellow);

            c.Text(2, 1, G("◢◣", "<>"), Palette.Pink, -1, true);
            c.Text(5, 1, "LOTUS", Palette.Cyan, -1, true);
            c.Text(12, 1, "//  LOCAL OPERATIONAL TRUSTED USB SYSTEM", Palette.Dim);
            c.TextRight(w - 3, 1, "v" + version, Palette.Dim);

            int x = 2;
            for (int i = 0; i < PageNames.Length; i++)
            {
                bool current = (int)Page == i;
                string label = " 0" + (i + 1) + " " + PageNames[i] + " ";
                c.Text(x, 2, label, current ? Palette.Ink : Palette.Dim, current ? Palette.Cyan : -1, current);
                x += label.Length + 1;
            }
            c.HLine(0, 3, w, Palette.Line, G("─", "-")[0]);
            c.HLine(0, c.Height - 2, w, Palette.Line, G("─", "-")[0]);
            c.Text(w - Math.Min(28, w), c.Height - 2, new string(stripe[0], Math.Min(28, w)), Palette.Yellow);

            // The status bar: counts on the left, the keys of this page on the right.
            int connected = live == null ? 0 : live.Devices.Count;
            int blocked = live == null ? 0 : live.Devices.Count(d => d.State == DeviceState.Blocked || d.State == DeviceState.Blocking);
            int trusted = live == null ? 0 : live.Policy.Trusted.Count;
            int at = 2;
            at = Counter(c, at, "CONNECTED", connected, Palette.Text);
            at = Counter(c, at, "BLOCKED", blocked, blocked > 0 ? Palette.Pink : Palette.Text);
            at = Counter(c, at, "TRUSTED", trusted, Palette.Cyan);
            // The keys of this page. The ones that work everywhere are left out when the row is short.
            int room = w - 3 - at;
            string keys = Keys(true);
            if (keys.Length > room) keys = Keys(false);
            c.TextRight(w - 3, c.Height - 1, Canvas.Shorten(keys, Math.Max(0, room), unicode), Palette.Dim);
        }

        private int Counter(Canvas c, int x, string label, int value, int color)
        {
            c.Text(x, c.Height - 1, label, Palette.Dim);
            c.Text(x + label.Length + 1, c.Height - 1, value.ToString(), color, -1, true);
            return x + label.Length + value.ToString().Length + 4;
        }

        private string Keys(bool all)
        {
            if (dialog != null) return dialog.Keys;
            string pages = all ? "  1-5 pages  q quit" : "";
            switch (Page)
            {
                case TuiPage.Control:
                    return G("↑↓ move  ←→ change", "arrows move and change") + "  a apply" + pages;
                case TuiPage.Devices:
                    return G("↑↓ choose", "arrows choose") + "  t trust  m model  u untrust" + pages;
                case TuiPage.Trusted:
                    return G("↑↓ choose", "arrows choose") + "  n add by hand  e end date  x remove" + pages;
                case TuiPage.Activity:
                    return G("↑↓ scroll", "arrows scroll") + pages;
                default:
                    return "1-5 pages  q quit";
            }
        }

        private void DrawUnavailable(Canvas c, int top)
        {
            int width = Math.Min(c.Width - 8, 72), x = (c.Width - width) / 2;
            c.Box(x, top + 2, width, 8, Palette.Pink, Palette.Panel, unicode);
            c.Text(x + 3, top + 3, "NO ANSWER FROM THE LOTUS DAEMON", Palette.Pink, -1, true);
            int row = top + 5;
            foreach (string line in Canvas.Wrap(error ?? "", width - 6)) c.Text(x + 3, row++, line, Palette.Text);
            c.Text(x + 3, top + 8, "This screen tries again every second.", Palette.Dim);
        }

        // ---------- control ----------

        private static readonly Dictionary<char, string[]> BigFont = new Dictionary<char, string[]>
        {
            ['A'] = new[] { "▄▀█", "█▀█" }, ['C'] = new[] { "█▀▀", "█▄▄" }, ['D'] = new[] { "█▀▄", "█▄▀" },
            ['E'] = new[] { "█▀▀", "██▄" }, ['I'] = new[] { "█", "█" }, ['K'] = new[] { "█▄▀", "█ █" },
            ['L'] = new[] { "█  ", "█▄▄" }, ['M'] = new[] { "█▀▄▀█", "█ ▀ █" }, ['N'] = new[] { "█▄ █", "█ ▀█" },
            ['O'] = new[] { "█▀█", "█▄█" }, ['R'] = new[] { "█▀█", "█▀▄" }, ['S'] = new[] { "█▀", "▄█" },
            ['W'] = new[] { "█ █ █", "▀▄▀▄▀" }, [' '] = new[] { "  ", "  " }
        };

        /// <summary>A word in letters two rows high, as two lines of text. The plain commands print these too.</summary>
        internal static string[] BigRows(string text)
        {
            var rows = new[] { new System.Text.StringBuilder(), new System.Text.StringBuilder() };
            foreach (char letter in text ?? "")
            {
                if (!BigFont.TryGetValue(letter, out string[] glyph)) continue;
                rows[0].Append(glyph[0]).Append(' ');
                rows[1].Append(glyph[1]).Append(' ');
            }
            return new[] { rows[0].ToString().TrimEnd(), rows[1].ToString().TrimEnd() };
        }

        /// <summary>The status in letters two rows high. Returns the width used.</summary>
        private int BigText(Canvas c, int x, int y, string text, int color)
        {
            if (!unicode)
            {
                c.Text(x, y, string.Join(" ", text.ToCharArray()), color, -1, true);
                c.Text(x, y + 1, new string('=', text.Length * 2 - 1), color);
                return text.Length * 2;
            }
            int at = x;
            foreach (char letter in text)
            {
                if (!BigFont.TryGetValue(letter, out string[] rows)) continue;
                c.Text(at, y, rows[0], color, -1, true);
                c.Text(at, y + 1, rows[1], color, -1, true);
                at += rows[0].Length + 1;
            }
            return at - x;
        }

        private void DrawControl(Canvas c, int top, int bottom)
        {
            int w = c.Width;
            int left = 3, right = w - 4;
            Policy shown = live.Lockdown ? GuardWorker.LockdownPolicy() : live.Policy;

            // ----- status and the master switch -----
            int statusEdge = live.Lockdown ? Palette.Pink : shown.Armed ? Palette.Cyan : Palette.Yellow;
            c.Fill(1, top, w - 2, 4, Palette.Panel);
            for (int row = 0; row < 4; row++) c.Text(1, top + row, G("▌", "|"), statusEdge);
            c.Text(left, top, "//  STATUS", Palette.Dim);
            string status = live.Lockdown ? "LOCKED DOWN" : shown.Armed ? "ARMED" : "DISARMED";
            int statusColor = live.Lockdown ? Palette.Pink : shown.Armed ? Palette.Cyan : Palette.Dim;
            BigText(c, left, top + 1, status, statusColor);
            string sentence = live.Lockdown
                ? "The policy store was damaged. Run the LOTUS installer again to reset it."
                : shown.Armed ? ArmedSentence(shown)
                : "Nothing is blocked. Choose your settings, switch LOTUS on and apply.";
            c.Text(left, top + 3, Canvas.Shorten(sentence, w - left - 4, unicode), Palette.Text);

            bool onSwitch = focus == FocusSwitch && !live.Lockdown;
            c.TextRight(right, top, "MASTER SWITCH", Palette.Dim);
            string knob = draft.Armed ? G("▕    ██▏", "[    ##]") : G("▕██    ▏", "[##    ]");
            c.TextRight(right, top + 1, knob, draft.Armed ? Palette.Cyan : Palette.Dim, Palette.Panel2, true);
            c.TextRight(right, top + 2, draft.Armed ? "      ON" : "     OFF", draft.Armed ? Palette.Cyan : Palette.Text, -1, true);
            if (onSwitch) c.TextRight(right - 9, top + 1, G("▶", ">"), Palette.Yellow, -1, true);

            // ----- the two rules -----
            int rules = top + 5;
            int half = (w - 5) / 2;
            int second = 2 + half + 1;
            c.Fill(1, rules, half, 8, Palette.Panel);
            c.Fill(second, rules, w - second - 1, 8, Palette.Panel);

            c.Text(left, rules, "REMOVABLE STORAGE", Palette.Text, -1, true);
            c.Text(left, rules + 1, Canvas.Shorten("Drives, disks, card readers, phones.", half - 4, unicode), Palette.Dim);
            DrawChoice(c, left, rules + 2, focus == FocusStorage,
                new[] { "ALLOW", "READ-ONLY", "BLOCK" }, (int)draft.Storage,
                new[] { Palette.Green, Palette.Yellow, Palette.Pink });
            string storageHint = draft.Storage == StorageMode.ReadOnly
                ? "Files can be opened and copied from a drive, but nothing can be saved to one. This applies to every USB disk, trusted or not."
                : draft.Storage == StorageMode.Block
                    ? "Drives, card readers and phones are switched off as soon as they are plugged in. Devices on the trusted list keep working."
                    : "USB drives, external disks, card readers and phones work normally.";
            DrawLines(c, left, rules + 4, half - 4, 4, storageHint, Palette.Text);

            int px = second + 2;
            c.Text(px, rules, "PNP DEVICES", Palette.Text, -1, true);
            c.Text(px, rules + 1, Canvas.Shorten("Every other device on a USB port.", w - px - 4, unicode), Palette.Dim);
            DrawChoice(c, px, rules + 2, focus == FocusPnp, new[] { "ALLOW", "TRUSTED ONLY" },
                draft.Pnp == PnpMode.Allow ? 0 : 1, new[] { Palette.Green, Palette.Pink });
            // Two lines of explanation: for the rule above, or for keyboards and mice while that choice is marked.
            string pnpHint = draft.Pnp == PnpMode.Allow
                ? "Any other USB device works when it is plugged in."
                : "Only devices on the trusted list work.";
            string inputHint = draft.Input == InputRule.Approve
                ? "A new keyboard or mouse waits until you approve it."
                : draft.Input == InputRule.Trusted
                    ? draft.Pnp == PnpMode.Block
                        ? "Keyboards and mice need trust like any other device."
                        : "No effect while other devices are allowed."
                    : "Any keyboard or mouse works, also one that types on its own.";
            DrawLines(c, px, rules + 4, w - px - 4, 2, focus == FocusInput ? inputHint : pnpHint, Palette.Text);
            c.Text(px, rules + 6, "KEYBOARDS AND MICE", Palette.Dim);
            DrawChoice(c, px, rules + 7, focus == FocusInput, new[] { "ALWAYS", "APPROVE", "TRUSTED" },
                (int)draft.Input, new[] { Palette.Green, Palette.Yellow, Palette.Pink });

            // ----- strict mode -----
            int strict = rules + 9;
            c.Fill(1, strict, w - 2, 2, Palette.Panel);
            DrawCheck(c, left, strict, focus == FocusStrict, draft.Strict, StrictOptionEnabled,
                "Refuse new devices before they start");
            string strictHint = !StrictOptionEnabled ? "Applies when something is blocked or has to be approved."
                : !draft.Strict ? "Off: a blocked device runs until LOTUS has switched it off."
                : live.Strict == "foreign" && live.Policy.Armed && live.Policy.Strict
                    ? "Another program holds the USB controllers shut, so LOTUS cannot. Blocked devices are switched off after they start."
                    : "The kernel holds every new device until LOTUS has looked at it. A device the policy blocks never runs.";
            c.Text(left + 4, strict + 1, Canvas.Shorten(strictHint, w - left - 8, unicode), Palette.Dim);

            // ----- apply -----
            int apply = strict + 3;
            bool onApply = focus == FocusApply;
            bool changes = HasChanges;
            string button = "  APPLY  ";
            c.Text(left, apply, button, changes ? Palette.Ink : Palette.Dim, changes ? Palette.Cyan : Palette.Panel2, true);
            if (onApply) c.Text(left - 2, apply, G("▶", ">"), Palette.Yellow, -1, true);
            string note = toast ?? (live.Lockdown ? "LOTUS takes no changes while it is locked down."
                : changes
                    ? draft.Armed || live.Policy.Armed
                        ? "You have changes that are not applied yet."
                        : "Your settings are saved once you apply. Switch LOTUS on to enforce them."
                    : "");
            c.Text(left + button.Length + 2, apply, Canvas.Shorten(note, w - left - button.Length - 6, unicode),
                toast != null ? toastColor : Palette.Yellow);

            // ----- latest activity, when there is room -----
            int logTop = apply + 2;
            if (bottom - logTop >= 2)
            {
                c.Text(left, logTop, "//  LATEST ACTIVITY", Palette.Dim);
                int rows = bottom - logTop;
                for (int i = 0; i < rows && i < log.Count; i++) DrawEvent(c, left, logTop + 1 + i, w - left - 3, log[i]);
            }
        }

        internal static string ArmedSentence(Policy policy)
        {
            string storage = policy.Storage == StorageMode.Block ? "Storage blocked."
                : policy.Storage == StorageMode.ReadOnly ? "Storage read-only." : "Storage allowed.";
            string devices = policy.Pnp == PnpMode.Allow ? "Other USB devices allowed."
                : policy.Input == InputRule.Always ? "Only trusted devices work, plus keyboards and mice."
                : policy.Input == InputRule.Approve ? "Only trusted devices work."
                : "Only trusted devices work, keyboards and mice included.";
            if (policy.ApproveInput) devices += " New keyboards and mice wait for approval.";
            return storage + " " + devices;
        }

        private void DrawLines(Canvas c, int x, int y, int width, int max, string text, int color)
        {
            List<string> lines = Canvas.Wrap(text, width);
            for (int i = 0; i < lines.Count && i < max; i++)
            {
                string line = i == max - 1 && lines.Count > max ? Canvas.Shorten(lines[i] + " ...", width, unicode) : lines[i];
                c.Text(x, y + i, line, color);
            }
        }

        private void DrawChoice(Canvas c, int x, int y, bool focused, string[] options, int selected, int[] colors)
        {
            if (focused) c.Text(x - 2, y, G("▶", ">"), Palette.Yellow, -1, true);
            int at = x;
            for (int i = 0; i < options.Length; i++)
            {
                string label = "  " + options[i] + "  ";
                bool on = i == selected;
                c.Text(at, y, label, on ? Palette.Ink : Palette.Dim, on ? colors[i] : Palette.Panel2, on);
                at += label.Length + 1;
            }
        }

        private void DrawCheck(Canvas c, int x, int y, bool focused, bool value, bool enabled, string label)
        {
            if (focused) c.Text(x - 2, y, G("▶", ">"), Palette.Yellow, -1, true);
            string box = value ? G("[■]", "[x]") : "[ ]";
            c.Text(x, y, box, !enabled ? Palette.Line : value ? Palette.Cyan : Palette.Dim, -1, true);
            c.Text(x + 4, y, label, enabled ? Palette.Text : Palette.Dim);
        }

        private void DrawEvent(Canvas c, int x, int y, int width, LotusEvent e)
        {
            int color = e.Type == EventTypes.Blocked || e.Type == EventTypes.Tamper || e.Type == EventTypes.Error
                ? Palette.Pink
                : e.Type == EventTypes.Warning ? Palette.Yellow : Palette.Cyan;
            c.Text(x, y, e.TimeUtc.ToLocalTime().ToString("dd MMM HH:mm:ss"), Palette.Dim);
            string type = (e.Type ?? "").ToUpperInvariant().Replace('_', ' ');
            c.Text(x + 17, y, Canvas.Shorten(type, 14, unicode), color, -1, true);
            c.Text(x + 33, y, Canvas.Shorten(e.Message, width - 33, unicode), Palette.Text);
        }

        // ---------- devices ----------

        private void DrawDevices(Canvas c, int top, int bottom)
        {
            int w = c.Width;
            c.Text(3, top, "DEVICES", Palette.Text, -1, true);
            c.Text(12, top, "What is plugged in, and what the policy says about each.", Palette.Dim);
            if (toast != null) c.Text(3, top + 1, Canvas.Shorten(toast, w - 6, unicode), toastColor);
            if (live.Devices.Count == 0)
            {
                c.Text(3, top + 3, "No USB device is connected.", Palette.Dim);
                return;
            }
            int listTop = top + 2;
            int perItem = 3;
            int visible = Math.Max(1, (bottom - 1 - listTop + 1) / perItem);
            int first = Math.Max(0, Math.Min(deviceIndex - visible / 2, live.Devices.Count - visible));
            DeviceStatus chosen = live.Devices[deviceIndex];
            c.Text(3, bottom, Canvas.Shorten(PolicyEngine.Explain(chosen.Decision, live.Policy), w - 6, unicode),
                chosen.Decision.IsBlock ? Palette.Pink : Palette.Cyan);
            for (int i = 0; i < visible && first + i < live.Devices.Count; i++)
            {
                DeviceStatus status = live.Devices[first + i];
                UsbDevice device = status.Device;
                bool selected = first + i == deviceIndex;
                int y = listTop + i * perItem;
                int bg = selected ? Palette.Panel2 : Palette.Panel;
                c.Fill(1, y, w - 2, 2, bg);

                string label;
                int color;
                switch (status.State)
                {
                    case DeviceState.Blocked:
                    case DeviceState.Blocking:
                        label = "BLOCKED";
                        color = Palette.Pink;
                        if (status.Decision.Reason == Reason.InputWaiting)
                        {
                            label = "PENDING";
                            color = Palette.Yellow;
                        }
                        break;
                    case DeviceState.Trusted:
                        label = "TRUSTED";
                        color = Palette.Cyan;
                        if (status.Decision.Trust != null && status.Decision.Trust.LetIn)
                        {
                            label = "LET IN";
                            color = Palette.Yellow;
                        }
                        break;
                    case DeviceState.OffElsewhere:
                        label = "OFF";
                        color = Palette.Dim;
                        break;
                    default:
                        label = device.Started ? "ALLOWED" : "WAITING";
                        color = device.Started ? Palette.Green : Palette.Yellow;
                        break;
                }
                c.Text(1, y, G("▌", "|"), color, bg);
                c.Text(1, y + 1, G("▌", "|"), color, bg);
                if (selected) c.Text(2, y, G("▶", ">"), Palette.Yellow, bg, true);
                c.Text(4, y, " " + label.PadRight(8), Palette.Ink, color, true);
                c.Text(15, y, Canvas.Shorten(device.Name, w - 46, unicode), Palette.Text, bg, true);
                c.TextRight(w - 4, y, Canvas.Shorten(string.Join(", ", UsbClassifier.Labels(device.Kinds)), 26, unicode), Palette.Dim, bg);
                string detail = device.InstanceId +
                                (device.BuiltIn ? "   built in" : "") +
                                (device.IsSystemDisk ? "   the system runs from it" : "");
                c.Text(15, y + 1, Canvas.Shorten(detail, w - 19, unicode), Palette.Dim, bg);
            }
            if (live.Devices.Count > visible)
            {
                c.TextRight(w - 4, top, (deviceIndex + 1) + " of " + live.Devices.Count, Palette.Dim);
            }
        }

        // ---------- trusted ----------

        internal static string KindLabel(TrustKind kind)
        {
            return kind == TrustKind.Vendor ? "WHOLE VENDOR" : kind == TrustKind.Model ? "WHOLE MODEL" : "THIS DEVICE";
        }

        private void DrawTrusted(Canvas c, int top, int bottom)
        {
            int w = c.Width;
            c.Text(3, top, "TRUSTED", Palette.Text, -1, true);
            c.Text(12, top, "Devices on this list are never blocked. Press n to add one by hand.", Palette.Dim);
            if (toast != null) c.Text(3, top + 1, Canvas.Shorten(toast, w - 6, unicode), toastColor);
            List<TrustEntry> entries = live.Policy.Trusted;
            if (entries.Count == 0)
            {
                c.Text(3, top + 3, "Nothing is trusted yet.", Palette.Dim);
                c.Text(3, top + 5, "Trust a device that is plugged in on the Devices page (press 2), or press n here", Palette.Dim);
                c.Text(3, top + 6, "to type in a vendor, a model or an instance path.", Palette.Dim);
                return;
            }
            int listTop = top + 2;
            int perItem = 3;
            int visible = Math.Max(1, (bottom - listTop + 1) / perItem);
            int first = Math.Max(0, Math.Min(trustIndex - visible / 2, entries.Count - visible));
            for (int i = 0; i < visible && first + i < entries.Count; i++)
            {
                TrustEntry entry = entries[first + i];
                bool selected = first + i == trustIndex;
                int y = listTop + i * perItem;
                int bg = selected ? Palette.Panel2 : Palette.Panel;
                c.Fill(1, y, w - 2, 2, bg);
                c.Text(1, y, G("▌", "|"), Palette.Cyan, bg);
                c.Text(1, y + 1, G("▌", "|"), Palette.Cyan, bg);
                if (selected) c.Text(2, y, G("▶", ">"), Palette.Yellow, bg, true);
                c.Text(4, y, Canvas.Shorten(string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed device" : entry.Name, w - 26, unicode),
                    Palette.Text, bg, true);
                c.TextRight(w - 4, y, " " + (entry.LetIn ? "NOT APPROVED" : KindLabel(entry.Kind)) + " ", Palette.Ink,
                    entry.LetIn ? Palette.Yellow : Palette.Cyan, true);
                bool connected = live.Devices.Any(d => entry.Matches(d.Device));
                string status = connected ? "CONNECTED" : "NOT CONNECTED";
                c.TextRight(w - 4, y + 1, status, connected ? Palette.Green : Palette.Dim, bg);
                int room = w - 10 - status.Length;
                if (entry.UntilUtc.HasValue)
                {
                    // Trust with an end says so, in the colour of things that need a second look.
                    string until = entry.IsOver(DateTime.UtcNow) ? "ENDED" : "UNTIL " + entry.UntilText();
                    c.TextRight(w - 6 - status.Length, y + 1, until, Palette.Yellow, bg, true);
                    room -= until.Length + 2;
                }
                c.Text(4, y + 1, Canvas.Shorten(entry.Id, Math.Max(8, room), unicode), Palette.Dim, bg);
            }
            if (entries.Count > visible) c.TextRight(w - 4, top, (trustIndex + 1) + " of " + entries.Count, Palette.Dim);
        }

        // ---------- activity ----------

        private void DrawActivity(Canvas c, int top, int bottom)
        {
            int w = c.Width;
            c.Text(3, top, "ACTIVITY", Palette.Text, -1, true);
            c.Text(13, top, "What LOTUS did, newest first.", Palette.Dim);
            if (log.Count == 0)
            {
                c.Text(3, top + 2, "The activity log is empty.", Palette.Dim);
                return;
            }
            int rows = bottom - top - 1;
            logScroll = Math.Max(0, Math.Min(logScroll, Math.Max(0, log.Count - rows)));
            for (int i = 0; i < rows && logScroll + i < log.Count; i++)
            {
                DrawEvent(c, 3, top + 2 + i, w - 6, log[logScroll + i]);
            }
            if (log.Count > rows) c.TextRight(w - 4, top, (logScroll + 1) + " of " + log.Count, Palette.Dim);
        }

        // ---------- about ----------

        private void DrawAbout(Canvas c, int top, int bottom)
        {
            int w = c.Width;
            var lines = new List<KeyValuePair<string, int>>();
            void Title(string text) { lines.Add(new KeyValuePair<string, int>("", 0)); lines.Add(new KeyValuePair<string, int>(text, Palette.Cyan)); }
            void Body(string text) { foreach (string line in Canvas.Wrap(text, w - 8)) lines.Add(new KeyValuePair<string, int>(line, Palette.Text)); }

            lines.Add(new KeyValuePair<string, int>("LOTUS decides which USB devices are allowed to work on this computer.", Palette.Text));
            Title("PROTECTION");
            Body("The policy and the trusted list are kept in an encrypted store that only root can open. If someone changes or deletes it, the daemon puts its own copy back and notes it in the activity log.");
            Title("HOW IT BLOCKS");
            Body("The kernel has a switch for every USB device. LOTUS reads what a device says it is and switches a device the policy blocks off there. Such a device has no driver and none of its functions.");
            Body("With \"Refuse new devices before they start\" on, the kernel holds every new device until LOTUS has looked at it, so a blocked device never runs. Read-only mode marks USB disks read-only in the kernel before anything can mount them.");
            Title("WHAT IT DOES NOT DO");
            Body("LOTUS trusts a device by what the device reports: vendor, product, serial number and functions. A device built to copy all of those from a trusted one is treated as that device.");
            Body("Root can change the policy and can remove LOTUS. From power-on until the daemon starts, the kernel runs every device that is plugged in.");
            Title("LOTUS " + version);
            Body("Local Operational Trusted USB System. Open source under the MIT License. It comes as it is, without warranty, and you use it at your own risk.");
            Body("github.com/MAXIVA11/LOTUS");

            int rows = bottom - top + 1;
            aboutScroll = Math.Max(0, Math.Min(aboutScroll, Math.Max(0, lines.Count - rows)));
            for (int i = 0; i < rows && aboutScroll + i < lines.Count; i++)
            {
                KeyValuePair<string, int> line = lines[aboutScroll + i];
                c.Text(3, top + i, line.Key, line.Value, -1, line.Value == Palette.Cyan);
            }
        }
    }

    // ================= dialogs =================

    internal abstract class Dialog
    {
        public bool Unicode = true;
        public abstract string Keys { get; }
        /// <summary>Returns true when the dialog is finished and goes away.</summary>
        public abstract bool Key(ConsoleKeyInfo key);
        public abstract void Render(Canvas c);

        protected static void Button(Canvas c, int x, int y, string label, bool focused, int color)
        {
            string text = "  " + label + "  ";
            c.Text(x, y, text, focused ? Palette.Ink : color, focused ? color : Palette.Panel2, true);
        }
    }

    internal sealed class MessageDialog : Dialog
    {
        public string Title = "";
        public string Text = "";

        public override string Keys => "enter close";

        public override bool Key(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Spacebar;
        }

        public override void Render(Canvas c)
        {
            int width = Math.Min(c.Width - 8, 70);
            List<string> lines = Canvas.Wrap(Text, width - 6);
            int height = lines.Count + 6;
            int x = (c.Width - width) / 2, y = Math.Max(1, (c.Height - height) / 2);
            c.Box(x, y, width, height, Palette.Pink, Palette.Panel, Unicode);
            c.Text(x + 3, y + 1, Title, Palette.Pink, -1, true);
            for (int i = 0; i < lines.Count; i++) c.Text(x + 3, y + 3 + i, lines[i], Palette.Text);
            Button(c, x + 3, y + height - 2, "CLOSE", true, Palette.Cyan);
        }
    }

    /// <summary>Lists what a change would do and asks before it is made.</summary>
    internal sealed class ConfirmDialog : Dialog
    {
        public string Title = "";
        public string Intro = "";
        public string CheckText;
        public bool Checked;
        public string Warning;
        public string ConfirmText = "APPLY";
        public bool Danger;
        public Action<bool> OnConfirm;

        private readonly List<KeyValuePair<string, List<string>>> sections = new List<KeyValuePair<string, List<string>>>();
        // 0 is the tick box when there is one, then the two buttons. The safe answer has the focus at first.
        private int focus = -1;

        public override string Keys => "tab move  space tick  enter choose  esc cancel";

        public void AddSection(string caption, List<string> items)
        {
            if (items != null && items.Count > 0) sections.Add(new KeyValuePair<string, List<string>>(caption, items));
        }

        private int Items => CheckText != null ? 3 : 2;
        private int ConfirmIndex => CheckText != null ? 1 : 0;
        private int CancelIndex => ConfirmIndex + 1;

        public override bool Key(ConsoleKeyInfo key)
        {
            if (focus < 0) focus = Danger ? CancelIndex : ConfirmIndex;
            char c = char.ToLowerInvariant(key.KeyChar);
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    return true;
                case ConsoleKey.Tab:
                case ConsoleKey.RightArrow:
                case ConsoleKey.DownArrow:
                    focus = (focus + 1) % Items;
                    return false;
                case ConsoleKey.LeftArrow:
                case ConsoleKey.UpArrow:
                    focus = (focus + Items - 1) % Items;
                    return false;
                case ConsoleKey.Spacebar:
                    if (CheckText != null && focus == 0) Checked = !Checked;
                    return false;
                case ConsoleKey.Enter:
                    if (CheckText != null && focus == 0)
                    {
                        Checked = !Checked;
                        return false;
                    }
                    if (focus == ConfirmIndex) OnConfirm?.Invoke(CheckText != null && Checked);
                    return true;
            }
            if (c == 'y')
            {
                OnConfirm?.Invoke(CheckText != null && Checked);
                return true;
            }
            return c == 'n';
        }

        public override void Render(Canvas c)
        {
            if (focus < 0) focus = Danger ? CancelIndex : ConfirmIndex;
            int width = Math.Min(c.Width - 6, 76);
            var body = new List<KeyValuePair<string, int>>();
            foreach (string line in Canvas.Wrap(Intro, width - 6)) body.Add(new KeyValuePair<string, int>(line, Palette.Text));
            foreach (KeyValuePair<string, List<string>> section in sections)
            {
                body.Add(new KeyValuePair<string, int>("", 0));
                body.Add(new KeyValuePair<string, int>(section.Key, Palette.Dim));
                int shown = 0;
                foreach (string item in section.Value)
                {
                    if (shown == 5 && section.Value.Count > 6)
                    {
                        body.Add(new KeyValuePair<string, int>("    and " + (section.Value.Count - 5) + " more", Palette.Dim));
                        break;
                    }
                    body.Add(new KeyValuePair<string, int>("  " + (Unicode ? "▪ " : "* ") + Canvas.Shorten(item, width - 12, Unicode), Palette.Text));
                    shown++;
                }
            }
            if (!string.IsNullOrEmpty(Warning))
            {
                body.Add(new KeyValuePair<string, int>("", 0));
                foreach (string line in Canvas.Wrap(Warning, width - 6)) body.Add(new KeyValuePair<string, int>(line, Palette.Yellow));
            }
            int extra = CheckText != null ? 2 : 0;
            int maxBody = Math.Max(3, c.Height - 10 - extra);
            if (body.Count > maxBody) body = body.GetRange(0, maxBody);
            int height = body.Count + 6 + extra;
            int x = (c.Width - width) / 2, y = Math.Max(1, (c.Height - height) / 2);
            int edge = Danger ? Palette.Pink : Palette.Cyan;
            c.Box(x, y, width, height, edge, Palette.Panel, Unicode);
            c.Text(x + 3, y + 1, Title, edge, -1, true);
            int row = y + 3;
            foreach (KeyValuePair<string, int> line in body) c.Text(x + 3, row++, line.Key, line.Value);
            if (CheckText != null)
            {
                row++;
                bool on = focus == 0;
                if (on) c.Text(x + 1, row, Unicode ? "▶" : ">", Palette.Yellow, -1, true);
                c.Text(x + 3, row, Checked ? (Unicode ? "[■]" : "[x]") : "[ ]", Checked ? Palette.Cyan : Palette.Dim, -1, true);
                c.Text(x + 7, row, CheckText, Palette.Text);
            }
            int buttons = y + height - 2;
            Button(c, x + 3, buttons, ConfirmText, focus == ConfirmIndex, Danger ? Palette.Pink : Palette.Cyan);
            Button(c, x + 3 + ConfirmText.Length + 6, buttons, "CANCEL", focus == CancelIndex, Palette.Text);
        }
    }

    /// <summary>The form that gives a trust entry an end, or takes the end away.</summary>
    internal sealed class TermDialog : Dialog
    {
        private readonly TuiApp app;
        private readonly TrustEntry entry;
        private string typed = "";
        private string problem;

        public TermDialog(TuiApp app, TrustEntry entry)
        {
            this.app = app;
            this.entry = entry;
        }

        public override string Keys => "type a time  enter set  esc cancel";

        public override bool Key(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    return true;
                case ConsoleKey.Backspace:
                    if (typed.Length > 0) typed = typed.Substring(0, typed.Length - 1);
                    problem = null;
                    return false;
                case ConsoleKey.Enter:
                    problem = app.SetTerm(entry, typed);
                    return problem == null;
            }
            char c = key.KeyChar;
            if (c >= ' ' && c != 0x7F && typed.Length < 40)
            {
                typed += c;
                problem = null;
            }
            return false;
        }

        public override void Render(Canvas c)
        {
            int width = Math.Min(c.Width - 6, 72), height = 13;
            int x = (c.Width - width) / 2, y = Math.Max(1, (c.Height - height) / 2);
            c.Box(x, y, width, height, Palette.Cyan, Palette.Panel, Unicode);
            c.Text(x + 3, y + 1, "TRUST UNTIL", Palette.Cyan, -1, true);
            string who = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name;
            c.Text(x + 3, y + 2, Canvas.Shorten(who, width - 6, Unicode), Palette.Text);
            c.Text(x + 3, y + 3, entry.UntilUtc.HasValue ? "Now: until " + entry.UntilText() : "Now: no end", Palette.Dim);

            string hint = "A length of time from now, such as 12h, 7d or 2w, or a date such as 2026-12-04 or " +
                          "2026-12-04 17:00. Leave it empty for trust without an end.";
            List<string> lines = Canvas.Wrap(problem ?? hint, width - 6);
            for (int i = 0; i < lines.Count && i < 3; i++) c.Text(x + 3, y + 5 + i, lines[i], problem != null ? Palette.Pink : Palette.Dim);

            int fieldX = x + 3, fieldWidth = width - 6;
            c.Fill(fieldX, y + 9, fieldWidth, 1, Palette.Ink);
            c.Text(fieldX + 1, y + 9, typed, Palette.Text, Palette.Ink);
            c.Text(fieldX + 1 + typed.Length, y + 9, Unicode ? "▏" : "_", Palette.Cyan, Palette.Ink, true);

            Button(c, x + 3, y + height - 2, "SET", true, Palette.Cyan);
            c.Text(x + 3 + 9, y + height - 2, "esc cancels", Palette.Dim);
        }
    }

    /// <summary>The form for a trust entry that is typed in: a vendor, a model or an instance path.</summary>
    internal sealed class AddTrustDialog : Dialog
    {
        private readonly TuiApp app;
        private int kind = 1;
        private string id = "";
        private string name = "";
        private string problem;
        // 0 kind, 1 id, 2 name, 3 add, 4 cancel
        private int focus;

        public AddTrustDialog(TuiApp app)
        {
            this.app = app;
        }

        public override string Keys => Unicode
            ? "←→ kind  tab next field  enter add  esc cancel"
            : "arrows kind  tab next field  enter add  esc cancel";

        private TrustKind Kind => kind == 0 ? TrustKind.Vendor : kind == 1 ? TrustKind.Model : TrustKind.Device;

        public override bool Key(ConsoleKeyInfo key)
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    return true;
                case ConsoleKey.Tab:
                case ConsoleKey.DownArrow:
                    focus = (focus + ((key.Modifiers & ConsoleModifiers.Shift) != 0 ? 4 : 1)) % 5;
                    return false;
                case ConsoleKey.UpArrow:
                    focus = (focus + 4) % 5;
                    return false;
                case ConsoleKey.LeftArrow:
                    if (focus == 0) kind = Math.Max(0, kind - 1);
                    else if (focus == 4) focus = 3;
                    problem = null;
                    return false;
                case ConsoleKey.RightArrow:
                    if (focus == 0) kind = Math.Min(2, kind + 1);
                    else if (focus == 3) focus = 4;
                    problem = null;
                    return false;
                case ConsoleKey.Backspace:
                    if (focus == 1 && id.Length > 0) id = id.Substring(0, id.Length - 1);
                    if (focus == 2 && name.Length > 0) name = name.Substring(0, name.Length - 1);
                    problem = null;
                    return false;
                case ConsoleKey.Enter:
                    if (focus == 4) return true;
                    if (focus == 0)
                    {
                        focus = 1;
                        return false;
                    }
                    problem = app.AddTyped(Kind, id, name);
                    return problem == null;
            }
            char c = key.KeyChar;
            if (c >= ' ' && c != 0x7F)
            {
                if (focus == 0 && c == ' ') kind = (kind + 1) % 3;
                else if (focus == 0)
                {
                    // Someone who starts typing means the ID.
                    focus = 1;
                    id += c;
                }
                else if (focus == 1 && id.Length < 200) id += c;
                else if (focus == 2 && name.Length < 96) name += c;
                problem = null;
            }
            return false;
        }

        public override void Render(Canvas c)
        {
            int width = Math.Min(c.Width - 6, 76), height = 15;
            int x = (c.Width - width) / 2, y = Math.Max(1, (c.Height - height) / 2);
            c.Box(x, y, width, height, Palette.Cyan, Palette.Panel, Unicode);
            c.Text(x + 3, y + 1, "ADD BY HAND", Palette.Cyan, -1, true);
            c.Text(x + 3, y + 2, "For a device that is not plugged in.", Palette.Dim);

            string arrow = Unicode ? "▶" : ">";
            int row = y + 4;
            if (focus == 0) c.Text(x + 1, row, arrow, Palette.Yellow, -1, true);
            int at = x + 3;
            string[] kinds = { "VENDOR", "MODEL", "DEVICE" };
            for (int i = 0; i < 3; i++)
            {
                string label = "  " + kinds[i] + "  ";
                c.Text(at, row, label, i == kind ? Palette.Ink : Palette.Dim, i == kind ? Palette.Green : Palette.Panel2, i == kind);
                at += label.Length + 1;
            }

            string hint = kind == 0 ? "A vendor ID, for example 046D. Every device that vendor makes will work, also ones you have never seen."
                : kind == 1 ? "A vendor ID and a product ID, for example 046D:C52B. Every unit of that model will work."
                : "An instance path, for example USB\\VID_0781&PID_5567\\4C530001230505116353. That one device will work.";
            List<string> lines = Canvas.Wrap(problem ?? hint, width - 6);
            for (int i = 0; i < lines.Count && i < 2; i++) c.Text(x + 3, y + 6 + i, lines[i], problem != null ? Palette.Pink : Palette.Dim);

            Field(c, x + 3, y + 9, width - 6, "ID", id, focus == 1, arrow);
            Field(c, x + 3, y + 11, width - 6, "NAME", name, focus == 2, arrow);
            c.Text(x + 3 + 6 + (width - 6 - 6) - 9, y + 12, "optional", Palette.Dim);

            Button(c, x + 3, y + height - 2, "ADD", focus == 3 || focus == 1 || focus == 2, Palette.Cyan);
            Button(c, x + 3 + 9, y + height - 2, "CANCEL", focus == 4, Palette.Text);
        }

        private void Field(Canvas c, int x, int y, int width, string label, string value, bool focused, string arrow)
        {
            if (focused) c.Text(x - 2, y, arrow, Palette.Yellow, -1, true);
            c.Text(x, y, label, Palette.Dim);
            int fieldX = x + 6, fieldWidth = width - 6;
            c.Fill(fieldX, y, fieldWidth, 1, Palette.Ink);
            string shown = value.Length > fieldWidth - 2 ? value.Substring(value.Length - (fieldWidth - 2)) : value;
            c.Text(fieldX + 1, y, shown, Palette.Text, Palette.Ink);
            if (focused) c.Text(fieldX + 1 + shown.Length, y, Unicode ? "▏" : "_", Palette.Cyan, Palette.Ink, true);
        }
    }
}
