using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Lotus.Core;
using Lotus.Linux.Tui;

namespace Lotus.Linux
{
    /// <summary>
    /// One program for everything on Linux. "lotus daemon" is what systemd runs:
    /// it enforces the policy. Every other command talks to that daemon over its
    /// socket, which only root can open.
    /// </summary>
    internal static class Program
    {
        private static string socketPath = UnixControlServer.DefaultPath;
        private static string stateDir = LinuxPlatform.DefaultStateDir;
        private static string sysfsRoot = LinuxDeviceBackend.DefaultRoot;
        private static bool dev;
        private static bool yes;
        private static string entryName;
        /// <summary>What followed --for or --until: how long the trust being given should last.</summary>
        private static string termText;

        private static int Main(string[] args)
        {
            try
            {
                // What is printed is UTF-8, whatever the console would pick by itself.
                Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            }
            catch (Exception ex) when (ex is IOException || ex is System.Security.SecurityException)
            {
            }
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--socket" when i + 1 < args.Length: socketPath = args[++i]; break;
                    case "--state" when i + 1 < args.Length: stateDir = args[++i]; break;
                    case "--sysfs" when i + 1 < args.Length: sysfsRoot = args[++i]; break;
                    case "--dev": dev = true; break;
                    case "--yes":
                    case "-y": yes = true; break;
                    case "--name" when i + 1 < args.Length: entryName = args[++i]; break;
                    case "--for" when i + 1 < args.Length:
                    case "--until" when i + 1 < args.Length:
                        termText = args[++i];
                        break;
                    default: rest.Add(args[i]); break;
                }
            }

            // On its own, at a terminal, "lotus" opens the interactive interface.
            // In a script or a pipe it prints the status, as before.
            bool atTerminal = !Console.IsInputRedirected && !Console.IsOutputRedirected;
            string command = rest.Count > 0 ? rest[0].ToLowerInvariant() : atTerminal ? "ui" : "status";
            try
            {
                switch (command)
                {
                    case "daemon": return RunDaemon();
                    case "ui": return RunUi(rest, atTerminal);
                    case "status": return rest.Contains("--json") ? StatusJson() : Status();
                    case "devices": return Devices();
                    case "arm": return Change(p => p.Armed = true, "LOTUS is armed.");
                    case "disarm": return Change(p => p.Armed = false, "LOTUS is disarmed. Nothing is blocked.");
                    case "storage": return SetStorage(Argument(rest, 1));
                    case "usb": return SetUsb(Argument(rest, 1));
                    case "input": return SetSwitch(Argument(rest, 1), (p, on) => p.AllowInput = on,
                        "Keyboards and mice always work.", "Keyboards and mice need to be trusted like any other device.");
                    case "strict": return SetSwitch(Argument(rest, 1), (p, on) => p.Strict = on,
                        "New devices the policy blocks are not switched on at all.",
                        "New devices the policy blocks are switched off right after they start.");
                    case "trust": return Trust(rest);
                    case "trusted": return TrustedList();
                    case "untrust": return Untrust(Argument(rest, 1));
                    case "log": return ShowLog(Argument(rest, 1));
                    case "release-all": return ReleaseAll();
                    case "remove-data": return RemoveData();
                    case "locked": return Read().Lockdown ? 0 : 1; // for the installer
                    case "version":
                    case "--version":
                        Console.WriteLine("LOTUS " + LotusPaths.Version);
                        return 0;
                    case "help":
                    case "--help":
                    case "-h":
                        Help();
                        return 0;
                    default:
                        Ui.Error("Unknown command \"" + rest[0] + "\". Run \"lotus help\" for the list.");
                        return 2;
                }
            }
            catch (GuardUnavailableException ex)
            {
                Ui.Error(ex.Message);
                return 1;
            }
            catch (FormatException ex)
            {
                Ui.Error("The LOTUS daemon sent something that could not be read. " + ex.Message);
                return 1;
            }
        }

        private static string Argument(List<string> rest, int index)
        {
            for (int i = 1, seen = 0; i < rest.Count; i++)
            {
                if (rest[i].StartsWith("--", StringComparison.Ordinal)) continue;
                if (++seen == index) return rest[i];
            }
            return null;
        }

        // ================= the daemon =================

        private static int RunDaemon()
        {
            LotusPaths.OverrideDataDir(stateDir);
            var options = new GuardOptions { PipeName = socketPath };
            if (dev)
            {
                // For developers and tests: a made-up device tree, a state folder of
                // its own and a socket anyone may use. Nothing of the real system is touched.
                if (sysfsRoot == LinuxDeviceBackend.DefaultRoot || stateDir == LinuxPlatform.DefaultStateDir ||
                    socketPath == UnixControlServer.DefaultPath)
                {
                    Ui.Error("The developer mode needs places of its own: --sysfs, --state and --socket.");
                    return 2;
                }
                // Its socket is open to every user, so it must never sit on top of the real devices.
                string tree = Path.GetFullPath(sysfsRoot).Replace('\\', '/');
                if (tree == "/sys" || tree.StartsWith("/sys/", StringComparison.Ordinal))
                {
                    Ui.Error("The developer mode works on a made-up device tree, not on /sys.");
                    return 2;
                }
                options.Platform = new LinuxPlatform(sysfsRoot, Path.Combine(stateDir, "99-lotus-readonly.rules"));
                options.AdministratorsOnly = false;
                options.TakeResetRequest = () => false;
            }
            else if (!IsRoot())
            {
                Ui.Error("The LOTUS daemon has to run as root. It is started by systemd: systemctl start lotus");
                return 1;
            }
            else
            {
                options.Platform = new LinuxPlatform(sysfsRoot);
            }

            using (var worker = new GuardWorker(options))
            using (var stopped = new ManualResetEventSlim(false))
            {
                // Removing LOTUS ends the daemon from the inside, after it has released every device.
                worker.StopRequested += purpose => stopped.Set();
                using (PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stopped.Set(); }))
                using (PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stopped.Set(); }))
                {
                    worker.Start();
                    if (!worker.WaitUntilReady(30000))
                    {
                        Ui.Error("The LOTUS daemon did not come up.");
                        return 1;
                    }
                    Console.WriteLine("LOTUS daemon " + LotusPaths.Version + " is running.");
                    stopped.Wait();
                    worker.Stop();
                }
            }
            return 0;
        }

        [DllImport("libc")]
        private static extern uint geteuid();

        private static bool IsRoot()
        {
            try
            {
                return OperatingSystem.IsWindows() || geteuid() == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return true;
            }
        }

        // ================= the interactive interface =================

        private static int RunUi(List<string> rest, bool atTerminal)
        {
            LotusPaths.OverrideDataDir(stateDir);
            var session = new DaemonSession(Read, next => Client().Apply(next));

            // "lotus ui --frame" draws one screen and ends. The tests and the
            // pictures in the documentation use it: --size 100x30, --keys "2 down t", --plain.
            if (rest.Contains("--frame"))
            {
                int width = 100, height = 30;
                string size = After(rest, "--size");
                if (size != null)
                {
                    string[] parts = size.ToLowerInvariant().Split('x');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height) ||
                        width < 20 || height < 5 || width > 400 || height > 200)
                    {
                        Ui.Error("Give the size as columns and rows, for example 100x30.");
                        return 2;
                    }
                }
                return TuiRunner.Frame(session, LotusPaths.Version, width, height, After(rest, "--keys"),
                    rest.Contains("--plain"), !rest.Contains("--ascii"));
            }
            if (!atTerminal)
            {
                Ui.Error("The interactive interface needs a terminal. In a script, use the commands: lotus help");
                return 2;
            }
            return TuiRunner.Run(session, LotusPaths.Version);
        }

        private static string After(List<string> rest, string option)
        {
            int index = rest.IndexOf(option);
            return index >= 0 && index + 1 < rest.Count ? rest[index + 1] : null;
        }

        // ================= reading =================

        internal sealed class State
        {
            public Policy Policy;
            public Ledger Ledger;
            public bool Lockdown;
            public string Strict;
            public List<DeviceStatus> Devices = new List<DeviceStatus>();
        }

        private static UnixControlClient Client()
        {
            return new UnixControlClient(socketPath);
        }

        internal static State Read()
        {
            GuardReply reply = Client().Read();
            if (!reply.Ok) throw new GuardUnavailableException(reply.Message.Length > 0 ? reply.Message : "The LOTUS daemon refused.");
            var state = new State
            {
                Policy = reply.ReadPolicy() ?? new Policy(),
                Ledger = reply.ReadLedger(),
                Lockdown = reply.Lockdown,
                Strict = Json.GetString(reply.Data, "strictState", "")
            };
            if (state.Lockdown) state.Policy = GuardWorker.LockdownPolicy();
            var backend = new LinuxDeviceBackend(sysfsRoot)
            {
                IsBlockedByLotus = state.Ledger.Contains
            };
            foreach (UsbDevice device in backend.Scan())
            {
                state.Devices.Add(DeviceView.Resolve(state.Policy, device, state.Ledger, TimeSpan.MaxValue));
            }
            return state;
        }

        private static int Status()
        {
            State state = Read();
            Ui.Banner();
            string word = state.Lockdown ? "LOCKED DOWN" : state.Policy.Armed ? "ARMED" : "DISARMED";
            string wordColour = state.Lockdown ? Ui.Pink : state.Policy.Armed ? Ui.Cyan : Ui.Yellow;
            if (Ui.Fancy)
            {
                // At a terminal the status is spelled out in large letters, as in the interface.
                foreach (string row in TuiApp.BigRows(word)) Console.WriteLine("  " + Ui.Paint(row, "1;" + wordColour));
                Console.WriteLine();
            }
            Ui.Line("STATUS", word, wordColour);
            if (state.Lockdown)
            {
                Ui.Note("The policy store was damaged or removed. Storage and untrusted USB devices are blocked.");
                Ui.Note("Run the LOTUS installer again to reset it.");
            }
            Ui.Line("STORAGE", state.Policy.Storage == StorageMode.Block ? "BLOCK"
                : state.Policy.Storage == StorageMode.ReadOnly ? "READ-ONLY" : "ALLOW",
                state.Policy.Storage == StorageMode.Allow ? Ui.Green : Ui.Pink);
            Ui.Line("USB DEVICES", state.Policy.Pnp == PnpMode.Block ? "TRUSTED ONLY" : "ALLOW",
                state.Policy.Pnp == PnpMode.Block ? Ui.Pink : Ui.Green);
            Ui.Line("KEYBOARDS AND MICE", state.Policy.AllowInput ? "ALWAYS WORK" : "NEED TRUST", Ui.Text);
            Ui.Line("NEW DEVICES", StrictText(state), Ui.Text);
            if (KernelHoldsAtStart())
            {
                // The kernel was started with usbcore.authorized_default=2.
                Ui.Line("AT START", "DEVICES WAIT FOR LOTUS", Ui.Text);
            }
            Ui.Line("TRUSTED", state.Policy.Trusted.Count.ToString(), Ui.Cyan);
            int blocked = state.Devices.Count(d => d.State == DeviceState.Blocked || d.State == DeviceState.Blocking);
            Ui.Line("CONNECTED", state.Devices.Count + (blocked > 0 ? "   BLOCKED " + blocked : ""), blocked > 0 ? Ui.Pink : Ui.Text);
            if (!state.Policy.Armed && !state.Lockdown)
            {
                Ui.Note("Nothing is blocked while LOTUS is disarmed. Choose the rules, then run: lotus arm");
            }
            if (Ui.Fancy)
            {
                Console.WriteLine();
                Ui.Note("Run \"lotus\" on its own for the interactive interface.");
            }
            return 0;
        }

        /// <summary>
        /// The status as one JSON object, for monitoring and compliance tools.
        /// The field names stay the same from version to version. The exit code
        /// is 0 when the daemon answers and 1 when it does not, and in both cases
        /// what is printed is JSON.
        /// </summary>
        private static int StatusJson()
        {
            var report = new Dictionary<string, object> { ["version"] = LotusPaths.Version };
            State state;
            try
            {
                state = Read();
            }
            catch (Exception ex) when (ex is GuardUnavailableException || ex is FormatException)
            {
                report["running"] = false;
                report["error"] = ex.Message;
                Console.WriteLine(Json.Write(report));
                return 1;
            }
            LotusPaths.OverrideDataDir(stateDir);
            DateTime? tamper = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile).LastOf(EventTypes.Tamper);
            DateTime now = DateTime.UtcNow;
            report["running"] = true;
            report["armed"] = state.Policy.Armed;
            report["lockdown"] = state.Lockdown;
            report["storage"] = state.Policy.Storage == StorageMode.Block ? "block"
                : state.Policy.Storage == StorageMode.ReadOnly ? "readonly" : "allow";
            report["usb"] = state.Policy.Pnp == PnpMode.Block ? "block" : "allow";
            report["keepInput"] = state.Policy.AllowInput;
            report["strict"] = state.Policy.Strict;
            report["strictState"] = state.Strict;
            report["trusted"] = state.Policy.Trusted.Count(entry => !entry.IsOver(now));
            report["trustedWithEnd"] = state.Policy.Trusted.Count(entry => entry.UntilUtc.HasValue && !entry.IsOver(now));
            report["connected"] = state.Devices.Count;
            report["blocked"] = state.Devices.Count(d => d.State == DeviceState.Blocked || d.State == DeviceState.Blocking);
            report["lastTamper"] = tamper.HasValue ? Json.FormatTime(tamper.Value) : null;
            report["heldAtStart"] = KernelHoldsAtStart();
            Console.WriteLine(Json.Write(report));
            return 0;
        }

        /// <summary>True when the kernel holds back devices that are not built in until the daemon has looked at them.</summary>
        private static bool KernelHoldsAtStart()
        {
            return sysfsRoot == LinuxDeviceBackend.DefaultRoot && LinuxUsbGate.HoldsAtStart(LinuxUsbGate.KernelSetting);
        }

        private static string StrictText(State state)
        {
            if (!state.Policy.Strict) return "SWITCHED OFF AFTER THEY START";
            if (state.Strict == "foreign") return "STRICT MODE UNAVAILABLE (CONTROLLERS HELD BY ANOTHER PROGRAM)";
            if (state.Strict == "unsupported") return "STRICT MODE UNAVAILABLE ON THIS KERNEL";
            return state.Strict == "on" ? "REFUSED BEFORE THEY START" : "REFUSED BEFORE THEY START, WHEN ARMED";
        }

        private static int Devices()
        {
            State state = Read();
            Ui.Banner();
            if (state.Devices.Count == 0)
            {
                Ui.Note("No USB device is connected.");
                return 0;
            }
            int number = 1;
            foreach (DeviceStatus status in state.Devices)
            {
                UsbDevice device = status.Device;
                string label;
                string colour;
                switch (status.State)
                {
                    case DeviceState.Blocked:
                    case DeviceState.Blocking:
                        label = "BLOCKED";
                        colour = Ui.Pink;
                        break;
                    case DeviceState.Trusted:
                        label = "TRUSTED";
                        colour = Ui.Cyan;
                        break;
                    case DeviceState.OffElsewhere:
                        label = "OFF";
                        colour = Ui.Dim;
                        break;
                    default:
                        label = device.Started ? "ALLOWED" : "WAITING";
                        colour = device.Started ? Ui.Green : Ui.Yellow;
                        break;
                }
                Console.WriteLine(Ui.Paint(number.ToString().PadLeft(3), Ui.Dim) + "  " + Ui.Chip(label.PadRight(8), colour) +
                                  "  " + Ui.Paint(Shorten(device.Name, 34).PadRight(34), "1;" + Ui.Text) + "  " +
                                  Ui.Paint(string.Join(", ", UsbClassifier.Labels(device.Kinds)), Ui.Dim));
                Console.WriteLine("     " + Ui.Paint(device.InstanceId, Ui.Dim) +
                                  (device.BuiltIn ? Ui.Paint("  built in", Ui.Dim) : "") +
                                  (device.IsSystemDisk ? Ui.Paint("  the system runs from it", Ui.Dim) : ""));
                if (status.State == DeviceState.Blocked || status.State == DeviceState.Blocking)
                {
                    Console.WriteLine("     " + Ui.Paint(PolicyEngine.Explain(status.Decision, state.Policy), Ui.Dim));
                }
                number++;
            }
            Console.WriteLine();
            Ui.Note("Trust a device with its number: lotus trust 2   Trust every unit of that model: lotus trust 2 --model");
            return 0;
        }

        private static string Shorten(string text, int length)
        {
            text = text ?? "";
            if (text.Length <= length) return text;
            // The mark for "cut off" is one character where the terminal can show it.
            return TuiRunner.TerminalShowsUnicode() ? text.Substring(0, length - 1) + "…" : text.Substring(0, length - 2) + "..";
        }

        private static int ShowLog(string countText)
        {
            int count = 20;
            if (countText != null && (!int.TryParse(countText, out count) || count < 1 || count > 1000))
            {
                Ui.Error("Give the number of lines to show, from 1 to 1000.");
                return 2;
            }
            LotusPaths.OverrideDataDir(stateDir);
            List<LotusEvent> events = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile).ReadRecent(count);
            events.Reverse();
            foreach (LotusEvent e in events)
            {
                string colour = e.Type == EventTypes.Blocked || e.Type == EventTypes.Tamper || e.Type == EventTypes.Error
                    ? Ui.Pink
                    : e.Type == EventTypes.Warning ? Ui.Yellow : Ui.Cyan;
                Console.WriteLine(Ui.Paint(e.TimeUtc.ToLocalTime().ToString("dd MMM HH:mm:ss"), Ui.Dim) + "  " +
                                  Ui.Paint(e.Type.ToUpperInvariant().PadRight(14), colour) + "  " + e.Message);
            }
            if (events.Count == 0) Ui.Note("The activity log is empty.");
            return 0;
        }

        // ================= changing =================

        private static int Apply(Policy next, string done)
        {
            GuardReply reply = Client().Apply(next);
            if (!reply.Ok)
            {
                Ui.Error(reply.Message.Length > 0 ? reply.Message : "The LOTUS daemon refused the change.");
                return 1;
            }
            Ui.Ok(done);
            return 0;
        }

        /// <summary>
        /// Changes the policy. If the change would switch devices off that work
        /// right now, they are listed first and the change needs a yes.
        /// </summary>
        private static int Change(Action<Policy> edit, string done)
        {
            State state = Read();
            if (state.Lockdown)
            {
                Ui.Error("LOTUS is locked down because its policy store was damaged. Run the LOTUS installer again to reset it.");
                return 1;
            }
            Policy next = state.Policy.Clone();
            edit(next);

            var lost = new List<UsbDevice>();
            foreach (DeviceStatus status in state.Devices)
            {
                UsbDevice device = status.Device;
                if (!device.Started) continue;
                if (PolicyEngine.Evaluate(next, device, TimeSpan.MaxValue).IsBlock) lost.Add(device);
            }
            // What is plugged into a hub goes down with the hub, whatever the policy says about it.
            var behind = new List<UsbDevice>();
            foreach (UsbDevice hub in lost.Where(d => d.IsHub).ToList())
            {
                foreach (DeviceStatus status in state.Devices)
                {
                    UsbDevice device = status.Device;
                    if (device.Started && !lost.Contains(device) &&
                        device.Location.StartsWith(hub.Location + ".", StringComparison.Ordinal))
                    {
                        lost.Add(device);
                        behind.Add(device);
                    }
                }
            }
            if (lost.Count > 0)
            {
                Ui.Warn(lost.Count == 1
                    ? "This switches off a device that works right now:"
                    : "This switches off " + lost.Count + " devices that work right now:");
                foreach (UsbDevice device in lost)
                {
                    Console.WriteLine("     " + Ui.Paint(Shorten(device.Name, 40), Ui.Text) + "  " +
                                      Ui.Paint(string.Join(", ", UsbClassifier.Labels(device.Kinds)), Ui.Dim) +
                                      (behind.Contains(device) ? Ui.Paint("  (plugged into a hub that is switched off)", Ui.Dim) : ""));
                }
                if (lost.Any(d => (d.Kinds & DeviceKinds.Input) != 0))
                {
                    Ui.Warn("One of them is a keyboard or a mouse. Trust your own first, or you may lock yourself out.");
                }
                if (!Confirm()) return 3;
            }
            return Apply(next, done);
        }

        private static bool Confirm()
        {
            if (yes) return true;
            if (Console.IsInputRedirected)
            {
                Ui.Error("Nothing was changed. Run the command again with --yes to go ahead.");
                return false;
            }
            Console.Write(Ui.Paint("  Go ahead? [y/N] ", Ui.Yellow));
            string answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (answer == "y" || answer == "yes") return true;
            Ui.Note("Nothing was changed.");
            return false;
        }

        private static int SetStorage(string value)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "allow": return Change(p => p.Storage = StorageMode.Allow, "Removable storage is allowed.");
                case "readonly":
                case "read-only": return Change(p => p.Storage = StorageMode.ReadOnly, "Removable storage is read-only.");
                case "block": return Change(p => p.Storage = StorageMode.Block, "Removable storage is blocked.");
                default:
                    Ui.Error("Say what to do with removable storage: lotus storage allow | readonly | block");
                    return 2;
            }
        }

        private static int SetUsb(string value)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "allow": return Change(p => p.Pnp = PnpMode.Allow, "Every USB device is allowed.");
                case "trusted":
                case "trusted-only": return Change(p => p.Pnp = PnpMode.Block, "Only trusted USB devices are allowed.");
                default:
                    Ui.Error("Say which USB devices may work: lotus usb allow | trusted");
                    return 2;
            }
        }

        private static int SetSwitch(string value, Action<Policy, bool> set, string whenOn, string whenOff)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "on": return Change(p => set(p, true), whenOn);
                case "off": return Change(p => set(p, false), whenOff);
                default:
                    Ui.Error("Say on or off.");
                    return 2;
            }
        }

        private static UsbDevice Pick(State state, string which)
        {
            if (which == null) return null;
            if (int.TryParse(which, out int number))
            {
                return number >= 1 && number <= state.Devices.Count ? state.Devices[number - 1].Device : null;
            }
            string id = Policy.NormalizeId(which);
            return state.Devices.Select(d => d.Device).FirstOrDefault(d => d.InstanceId == id);
        }

        /// <summary>
        /// "lotus trust 3" trusts a connected device. With a word in front it
        /// takes what was typed instead: a vendor, a model or an instance path,
        /// for a device that need not be plugged in.
        /// </summary>
        private static int Trust(List<string> rest)
        {
            if (termText != null && !TrustEntry.TryParseTerm(termText, DateTime.UtcNow, out DateTime? _, out string problem))
            {
                Ui.Error(problem);
                return 2;
            }
            string first = Argument(rest, 1);
            switch ((first ?? "").ToLowerInvariant())
            {
                case "vendor": return TrustTyped(TrustKind.Vendor, Argument(rest, 2));
                case "model": return TrustTyped(TrustKind.Model, Argument(rest, 2));
                case "id": return TrustTyped(TrustKind.Device, Argument(rest, 2));
                case "connected": return TrustConnected();
                default: return Trust(first, rest.Contains("--model"));
            }
        }

        private static int TrustTyped(TrustKind kind, string text)
        {
            if (!TrustEntry.TryParse(kind, text, entryName, out TrustEntry entry, out string problem))
            {
                Ui.Error(problem);
                return 2;
            }
            State state = Read();
            if (kind == TrustKind.Device)
            {
                // A device that is plugged in is known, so what it does is recorded with the entry.
                UsbDevice seen = state.Devices.Select(d => d.Device).FirstOrDefault(d => d.InstanceId == entry.Id);
                if (seen != null)
                {
                    TrustEntry recorded = TrustEntry.ForDevice(seen);
                    if (!string.IsNullOrWhiteSpace(entryName)) recorded.Name = entry.Name;
                    entry = recorded;
                }
            }
            Policy next = state.Policy.Clone();
            string what = kind == TrustKind.Vendor ? "Every device from vendor " + entry.Id.Substring(4)
                : kind == TrustKind.Model ? "Every " + entry.Id
                : entry.Id;
            switch (AddWithTerm(next, entry))
            {
                case Added.Nothing:
                    Ui.Note("That is on the trusted list already.");
                    return 0;
                case Added.NewEnd:
                    return Apply(next, EndSentence(what, entry));
            }
            if (kind == TrustKind.Vendor)
            {
                Ui.Note("This covers every device that vendor makes, also ones you have never seen.");
            }
            return Apply(next, what + " is trusted" + UntilWords(entry) + ".");
        }

        private enum Added
        {
            Nothing,
            Entry,
            NewEnd
        }

        /// <summary>
        /// Puts the entry on the list with the end that --for or --until asked
        /// for. An entry that is on the list already gets the new end, when one
        /// was given: that is how trust is made longer, shorter or endless.
        /// </summary>
        private static Added AddWithTerm(Policy next, TrustEntry entry)
        {
            DateTime? until = null;
            if (termText != null) TrustEntry.TryParseTerm(termText, DateTime.UtcNow, out until, out _);
            entry.UntilUtc = until;
            if (next.AddTrust(entry)) return Added.Entry;
            if (termText == null) return Added.Nothing;
            TrustEntry existing = next.Trusted.First(e => e.Kind == entry.Kind && e.Id == entry.Id);
            if (existing.UntilUtc == until) return Added.Nothing;
            existing.UntilUtc = until;
            return Added.NewEnd;
        }

        private static string UntilWords(TrustEntry entry)
        {
            return entry.UntilUtc.HasValue ? " until " + entry.UntilText() : "";
        }

        private static string EndSentence(string what, TrustEntry entry)
        {
            return entry.UntilUtc.HasValue
                ? "The trust for " + what + " now ends on " + entry.UntilText() + "."
                : "The trust for " + what + " has no end now.";
        }

        private static int TrustConnected()
        {
            State state = Read();
            Policy next = state.Policy.Clone();
            List<TrustEntry> added = next.TrustAll(state.Devices.Select(d => d.Device));
            DateTime? until = null;
            if (termText != null) TrustEntry.TryParseTerm(termText, DateTime.UtcNow, out until, out _);
            foreach (TrustEntry entry in added) entry.UntilUtc = until;
            if (added.Count == 0)
            {
                Ui.Note(state.Devices.Count == 0
                    ? "No USB device is connected."
                    : "Every connected USB device is on the trusted list already.");
                return 0;
            }
            foreach (TrustEntry entry in added)
            {
                Console.WriteLine("     " + Ui.Paint(Shorten(entry.Name, 40).PadRight(40), Ui.Text) + "  " +
                                  Ui.Paint(KindText(entry.Kind), Ui.Dim));
            }
            string end = until.HasValue ? " until " + added[0].UntilText() : "";
            return Apply(next, added.Count == 1
                ? "One connected device is trusted" + end + "."
                : added.Count + " connected devices are trusted" + end + ".");
        }

        private static string KindText(TrustKind kind)
        {
            return kind == TrustKind.Vendor ? "whole vendor" : kind == TrustKind.Model ? "whole model" : "this device";
        }

        private static int TrustedList()
        {
            State state = Read();
            Ui.Banner();
            if (state.Policy.Trusted.Count == 0)
            {
                Ui.Note("Nothing is trusted yet.");
                return 0;
            }
            foreach (TrustEntry entry in state.Policy.Trusted)
            {
                bool connected = state.Devices.Any(d => entry.Matches(d.Device));
                Console.WriteLine("  " + Ui.Paint(KindText(entry.Kind).ToUpperInvariant().PadRight(12), Ui.Cyan) + "  " +
                                  Ui.Paint(Shorten(string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed device" : entry.Name, 44), Ui.Text) +
                                  (connected ? Ui.Paint("  connected", Ui.Dim) : ""));
                Console.WriteLine("  " + "".PadRight(14) + Ui.Paint(entry.Id, Ui.Dim) +
                                  (entry.UntilUtc.HasValue ? "  " + Ui.Paint("until " + entry.UntilText(), Ui.Yellow) : ""));
            }
            Console.WriteLine();
            Ui.Note("Take one off the list with its ID: lotus untrust <id>");
            return 0;
        }

        private static int Trust(string which, bool model)
        {
            State state = Read();
            UsbDevice device = Pick(state, which);
            if (device == null)
            {
                Ui.Error("Name the device by its number or its ID from \"lotus devices\". For a device that is not " +
                         "plugged in: lotus trust vendor|model|id <value>.");
                return 2;
            }
            Policy next = state.Policy.Clone();
            TrustEntry entry = model ? TrustEntry.ForModel(device) : TrustEntry.ForDevice(device);
            string what = model ? "Every " + device.Name + " (" + device.ModelId + ")" : device.Name;
            switch (AddWithTerm(next, entry))
            {
                case Added.Nothing:
                    Ui.Note(device.Name + " is on the trusted list already.");
                    return 0;
                case Added.NewEnd:
                    return Apply(next, EndSentence(what, entry));
            }
            if (!model && !device.HasSerial)
            {
                Ui.Note("This device has no serial number, so the trust holds for the port it is in now.");
            }
            return Apply(next, what + " is trusted" + UntilWords(entry) + ".");
        }

        private static int Untrust(string which)
        {
            State state = Read();
            Policy next = state.Policy.Clone();
            int before = next.Trusted.Count;
            UsbDevice device = Pick(state, which);
            if (device != null)
            {
                next.RemoveTrust(TrustKind.Device, device.InstanceId);
                next.RemoveTrust(TrustKind.Model, device.ModelId);
            }
            else if (which != null)
            {
                // Not connected: go by what the trusted list itself says.
                next.RemoveTrust(TrustKind.Device, which);
                next.RemoveTrust(TrustKind.Model, which);
                next.RemoveTrust(TrustKind.Vendor, which);
                // Or by what was typed when the entry was made: 046D, 046D:C52B.
                foreach (TrustKind kind in new[] { TrustKind.Vendor, TrustKind.Model })
                {
                    if (next.Trusted.Count == before && TrustEntry.TryParse(kind, which, null, out TrustEntry typed, out _))
                    {
                        next.RemoveTrust(kind, typed.Id);
                    }
                }
            }
            if (next.Trusted.Count == before)
            {
                Ui.Error("Nothing on the trusted list matches. \"lotus trusted\" shows the list with the ID of every entry.");
                return 2;
            }
            return Change(p =>
            {
                p.Trusted.Clear();
                p.Trusted.AddRange(next.Trusted);
            }, "Removed from the trusted list.");
        }

        private static int ReleaseAll()
        {
            return Change(p => p.Armed = false, "LOTUS is disarmed. Every blocked device is switched back on.");
        }

        /// <summary>
        /// Used by the uninstall script: the daemon switches every device back on,
        /// opens the controllers again, erases its store and ends.
        /// </summary>
        private static int RemoveData()
        {
            GuardReply reply = Client().Stop(GuardProtocol.PurposeUninstall);
            if (!reply.Ok)
            {
                Ui.Error(reply.Message.Length > 0 ? reply.Message : "The LOTUS daemon refused.");
                return 1;
            }
            if (reply.Message.Length > 0) Ui.Warn(reply.Message);
            Ui.Ok("Every blocked device is switched back on and the policy is erased.");
            return 0;
        }

        private static void Help()
        {
            Ui.Banner();
            Console.WriteLine(@"  lotus                           Open the interactive interface: the pages of the Windows
                                  app in your terminal, driven with the keyboard.

  lotus status                    What is in force right now.
  lotus status --json             The same as one JSON object, for monitoring tools.
  lotus devices                   The connected USB devices and what the policy says about each.
  lotus log [lines]               The activity log.

  lotus storage allow|readonly|block
                                  USB drives, card readers, cameras and phones.
  lotus usb allow|trusted         Every other USB device: all of them, or only trusted ones.
  lotus input on|off              Keep keyboards and mice working under ""trusted"".
  lotus strict on|off             Refuse new blocked devices before they start. Off unless you turn it on.
  lotus arm                       Start enforcing the rules.
  lotus disarm                    Stop enforcing. Nothing is blocked.

  lotus trust <number|id>         Trust one device that is plugged in.
  lotus trust <number|id> --model Trust every unit of that model.
  lotus trust connected           Trust every device that is plugged in right now.
  lotus trust vendor <vid>        Trust by hand, for a device that need not be plugged in:
  lotus trust model <vid:pid>     every device of a vendor (046D), every unit of a model
  lotus trust id <instance path>  (046D:C52B), or one device by its path. --name ""..."" names it.
  lotus trust ... --for 7d        Trust that ends by itself: 90m, 12h, 7d, 2w from now, or
  lotus trust ... --until 2026-12-04
                                  on a date, with a time if you like (""2026-12-04 17:00"").
                                  Given for an entry that is on the list, it changes the
                                  end. ""--for never"" takes the end away.
  lotus trusted                   The trusted list.
  lotus untrust <number|id>       Take a device, a model or a vendor off the trusted list.

  lotus release-all               Disarm and switch every blocked device back on.

  A change that would switch off a device that works right now asks first.
  Add --yes to answer in advance. Every command needs root.");
        }
    }

    /// <summary>The look of the command line: neon on black, like the rest of LOTUS.</summary>
    internal static class Ui
    {
        public const string Cyan = "38;2;0;240;255";
        public const string Pink = "38;2;255;45;149";
        public const string Yellow = "38;2;252;238;10";
        public const string Green = "38;2;61;255;139";
        public const string Text = "38;2;244;244;246";
        public const string Dim = "38;2;141;141;163";

        // Colours at a terminal, none in a pipe. NO_COLOR switches them off and
        // CLICOLOR_FORCE=1 switches them on wherever the output goes.
        private static readonly bool Colour =
            Environment.GetEnvironmentVariable("CLICOLOR_FORCE") == "1" ||
            (!Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null &&
             Environment.GetEnvironmentVariable("TERM") != "dumb");

        /// <summary>Colours and a terminal that can show block characters: the output may look like the interface.</summary>
        public static readonly bool Fancy = Colour && TuiRunner.TerminalShowsUnicode();

        public static string Paint(string text, string colour)
        {
            return Colour ? "\u001b[" + colour + "m" + text + "\u001b[0m" : text;
        }

        /// <summary>A label on a coloured block, like the state of a device in the interface.</summary>
        public static string Chip(string text, string colour)
        {
            if (!Colour) return text;
            // The colour comes as a foreground code. The same numbers make the background.
            return "\u001b[1;38;2;8;8;12;48" + colour.Substring(2) + "m " + text.TrimEnd() + " \u001b[0m" +
                   new string(' ', Math.Max(0, text.Length - text.TrimEnd().Length));
        }

        public static void Banner()
        {
            Console.WriteLine();
            if (Fancy)
            {
                Console.WriteLine("  " + Paint(new string('▞', 28), Yellow));
                Console.WriteLine("  " + Paint("◢◣", "1;" + Pink) + " " + Paint("LOTUS", "1;" + Cyan) +
                                  Paint("  //  LOCAL OPERATIONAL TRUSTED USB SYSTEM", Dim));
            }
            else
            {
                Console.WriteLine("  " + Paint("LOTUS", "1;" + Cyan) + Paint("  //  LOCAL OPERATIONAL TRUSTED USB SYSTEM", Dim));
                Console.WriteLine("  " + Paint("/ / / / / / / / / / / / / / / / / / / / / / / /", Yellow));
            }
            Console.WriteLine();
        }

        public static void Line(string label, string value, string colour)
        {
            Console.WriteLine("  " + Paint(label.PadRight(20), Dim) + Paint(value, "1;" + colour));
        }

        public static void Note(string text)
        {
            Console.WriteLine("  " + Paint(text, Dim));
        }

        public static void Ok(string text)
        {
            Console.WriteLine("  " + Paint("OK", "1;" + Cyan) + "  " + text);
        }

        public static void Warn(string text)
        {
            Console.WriteLine("  " + Paint("!!", "1;" + Yellow) + "  " + text);
        }

        public static void Error(string text)
        {
            Console.Error.WriteLine("  " + Paint("XX", "1;" + Pink) + "  " + text);
        }
    }
}
