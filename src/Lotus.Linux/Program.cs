using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Lotus.Core;

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

        private static int Main(string[] args)
        {
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
                    default: rest.Add(args[i]); break;
                }
            }

            string command = rest.Count > 0 ? rest[0].ToLowerInvariant() : "status";
            try
            {
                switch (command)
                {
                    case "daemon": return RunDaemon();
                    case "status": return Status();
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

        // ================= reading =================

        private sealed class State
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

        private static State Read()
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
            if (state.Lockdown)
            {
                Ui.Line("STATUS", "LOCKED DOWN", Ui.Pink);
                Ui.Note("The policy store was damaged or removed. Storage and untrusted USB devices are blocked.");
                Ui.Note("Run the LOTUS installer again to reset it.");
            }
            else
            {
                Ui.Line("STATUS", state.Policy.Armed ? "ARMED" : "DISARMED", state.Policy.Armed ? Ui.Cyan : Ui.Yellow);
            }
            Ui.Line("STORAGE", state.Policy.Storage == StorageMode.Block ? "BLOCK"
                : state.Policy.Storage == StorageMode.ReadOnly ? "READ-ONLY" : "ALLOW",
                state.Policy.Storage == StorageMode.Allow ? Ui.Green : Ui.Pink);
            Ui.Line("USB DEVICES", state.Policy.Pnp == PnpMode.Block ? "TRUSTED ONLY" : "ALLOW",
                state.Policy.Pnp == PnpMode.Block ? Ui.Pink : Ui.Green);
            Ui.Line("KEYBOARDS AND MICE", state.Policy.AllowInput ? "ALWAYS WORK" : "NEED TRUST", Ui.Text);
            Ui.Line("NEW DEVICES", StrictText(state), Ui.Text);
            Ui.Line("TRUSTED", state.Policy.Trusted.Count.ToString(), Ui.Cyan);
            int blocked = state.Devices.Count(d => d.State == DeviceState.Blocked || d.State == DeviceState.Blocking);
            Ui.Line("CONNECTED", state.Devices.Count + (blocked > 0 ? "   BLOCKED " + blocked : ""), blocked > 0 ? Ui.Pink : Ui.Text);
            if (!state.Policy.Armed && !state.Lockdown)
            {
                Ui.Note("Nothing is blocked while LOTUS is disarmed. Choose the rules, then run: lotus arm");
            }
            return 0;
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
                Console.WriteLine(Ui.Paint(number.ToString().PadLeft(3), Ui.Dim) + "  " + Ui.Paint(label.PadRight(8), colour) +
                                  "  " + Ui.Paint(Shorten(device.Name, 34).PadRight(34), Ui.Text) + "  " +
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
            return text.Length <= length ? text : text.Substring(0, length - 1) + "…";
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
            if (!next.AddTrust(entry))
            {
                Ui.Note("That is on the trusted list already.");
                return 0;
            }
            if (kind == TrustKind.Vendor)
            {
                Ui.Note("This covers every device that vendor makes, also ones you have never seen.");
            }
            return Apply(next, kind == TrustKind.Vendor ? "Every device from vendor " + entry.Id.Substring(4) + " is trusted."
                : kind == TrustKind.Model ? "Every " + entry.Id + " is trusted."
                : entry.Id + " is trusted.");
        }

        private static int TrustConnected()
        {
            State state = Read();
            Policy next = state.Policy.Clone();
            List<TrustEntry> added = next.TrustAll(state.Devices.Select(d => d.Device));
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
            return Apply(next, added.Count == 1
                ? "One connected device is trusted."
                : added.Count + " connected devices are trusted.");
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
                Console.WriteLine("  " + "".PadRight(14) + Ui.Paint(entry.Id, Ui.Dim));
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
            if (!next.AddTrust(model ? TrustEntry.ForModel(device) : TrustEntry.ForDevice(device)))
            {
                Ui.Note(device.Name + " is on the trusted list already.");
                return 0;
            }
            if (!model && !device.HasSerial)
            {
                Ui.Note("This device has no serial number, so the trust holds for the port it is in now.");
            }
            return Apply(next, model
                ? "Every " + device.Name + " (" + device.ModelId + ") is trusted."
                : device.Name + " is trusted.");
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
            Console.WriteLine(@"  lotus status                    What is in force right now.
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

        private static readonly bool Colour =
            !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null &&
            Environment.GetEnvironmentVariable("TERM") != "dumb";

        public static string Paint(string text, string colour)
        {
            return Colour ? "\u001b[" + colour + "m" + text + "\u001b[0m" : text;
        }

        public static void Banner()
        {
            Console.WriteLine();
            Console.WriteLine("  " + Paint("LOTUS", "1;" + Cyan) + Paint("  //  LOCAL OPERATIONAL TRUSTED USB SYSTEM", Dim));
            Console.WriteLine("  " + Paint("/ / / / / / / / / / / / / / / / / / / / / / / /", Yellow));
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
