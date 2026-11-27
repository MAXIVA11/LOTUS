using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lotus.Core;
using Lotus.Linux;
using Xunit;

namespace Lotus.Linux.Tests
{
    /// <summary>What goes to the systemd journal.</summary>
    public class JournalLogTests
    {
        private static Dictionary<string, string> Fields(LotusEvent e)
        {
            string text = Encoding.UTF8.GetString(JournalLog.Format(e));
            Assert.EndsWith("\n", text);
            var fields = new Dictionary<string, string>();
            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                int equals = line.IndexOf('=');
                Assert.True(equals > 0, "a line without a field name: " + line);
                // The journal would take a second field of the same name as a second value.
                fields.Add(line.Substring(0, equals), line.Substring(equals + 1));
            }
            return fields;
        }

        [Fact]
        public void A_blocked_device_becomes_an_entry_with_fields_to_filter_on()
        {
            Dictionary<string, string> fields = Fields(new LotusEvent
            {
                Type = EventTypes.Blocked,
                Message = "SanDisk Cruzer was blocked because removable storage is blocked.",
                DeviceName = "SanDisk Cruzer",
                DeviceId = @"USB\VID_0781&PID_5567\4C53"
            });
            Assert.Equal("SanDisk Cruzer was blocked because removable storage is blocked. [SanDisk Cruzer]", fields["MESSAGE"]);
            Assert.Equal("4", fields["PRIORITY"]);
            Assert.Equal("lotus", fields["SYSLOG_IDENTIFIER"]);
            Assert.Equal("blocked", fields["LOTUS_EVENT"]);
            Assert.Equal("2100", fields["LOTUS_EVENT_ID"]);
            Assert.Equal(@"USB\VID_0781&PID_5567\4C53", fields["LOTUS_DEVICE_ID"]);
            Assert.Equal("SanDisk Cruzer", fields["LOTUS_DEVICE_NAME"]);
        }

        [Theory]
        [InlineData(EventTypes.Tamper, "3")]
        [InlineData(EventTypes.Error, "3")]
        [InlineData(EventTypes.Warning, "4")]
        [InlineData(EventTypes.Security, "4")]
        [InlineData(EventTypes.Released, "6")]
        [InlineData(EventTypes.GuardStarted, "6")]
        public void The_priority_follows_how_serious_the_event_is(string type, string priority)
        {
            Assert.Equal(priority, Fields(new LotusEvent { Type = type, Message = "x" })["PRIORITY"]);
        }

        [Fact]
        public void An_event_without_a_device_has_no_device_fields()
        {
            Dictionary<string, string> fields = Fields(new LotusEvent { Type = EventTypes.PolicyChanged, Message = "Policy changed: armed." });
            Assert.False(fields.ContainsKey("LOTUS_DEVICE_ID"));
            Assert.False(fields.ContainsKey("LOTUS_DEVICE_NAME"));
            Assert.Equal("Policy changed: armed.", fields["MESSAGE"]);
        }

        [Fact]
        public void A_device_cannot_forge_a_field_through_its_name()
        {
            Dictionary<string, string> fields = Fields(new LotusEvent
            {
                Type = EventTypes.Blocked,
                Message = "Blocked.",
                DeviceName = "Stick\nLOTUS_EVENT=released\nPRIORITY=7\n_UID=0",
                DeviceId = "USB\\VID_0001&PID_0002\\A\nMESSAGE=all is well"
            });
            Assert.Equal("blocked", fields["LOTUS_EVENT"]);
            Assert.Equal("4", fields["PRIORITY"]);
            Assert.False(fields.ContainsKey("_UID"));
            Assert.StartsWith("Blocked.", fields["MESSAGE"]);
            Assert.Equal(7, fields.Count);
        }

        [Fact]
        public void A_long_message_is_kept_whole()
        {
            string message = "The policy store cannot be trusted. " + new string('y', 500);
            Assert.Equal(message, Fields(new LotusEvent { Type = EventTypes.Tamper, Message = message })["MESSAGE"]);
        }

        [Fact]
        public void A_journal_that_is_not_there_is_not_a_problem()
        {
            string nowhere = Path.Combine(Path.GetTempPath(), "lotus-no-journal-" + Guid.NewGuid().ToString("N"));
            Assert.False(JournalLog.Available(nowhere));
            using (var journal = new JournalLog(nowhere))
            {
                journal.Write(new LotusEvent { Type = EventTypes.Blocked, Message = "x" });
                journal.Write(new LotusEvent { Type = EventTypes.Blocked, Message = "y" });
                journal.Write(null);
            }
        }
    }

    /// <summary>
    /// The kernel takes a device apart in steps. Its descriptors go a moment
    /// before its folder does, and in between it must not be judged.
    /// </summary>
    public class LeavingDeviceTests : IDisposable
    {
        private readonly FakeSysfs sys = new FakeSysfs();
        private readonly string folder = Path.Combine(Path.GetTempPath(), "lotus-leave-" + Guid.NewGuid().ToString("N"));
        private readonly string socket =
            Path.Combine(Path.GetTempPath(), "lotus-l-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".sock");
        private GuardWorker worker;

        public LeavingDeviceTests()
        {
            LotusPaths.OverrideDataDir(folder);
        }

        public void Dispose()
        {
            worker?.Dispose();
            LotusPaths.OverrideDataDir(null);
            sys.Dispose();
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                if (File.Exists(socket)) File.Delete(socket);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private string Descriptors(string device)
        {
            return Path.Combine(sys.Root, device, "descriptors");
        }

        [Fact]
        public void A_device_that_lost_its_descriptors_is_on_its_way_out_and_is_not_listed()
        {
            sys.PlugKeyboard();
            var backend = new LinuxDeviceBackend(sys.Root, () => new HashSet<string>());
            UsbDevice keyboard = Assert.Single(backend.Scan());
            Assert.True(keyboard.FunctionsKnown);

            File.Delete(Descriptors("1-1"));
            Assert.Empty(backend.Scan());

            // An empty file is what a read gives while the kernel is at it.
            File.WriteAllBytes(Descriptors("1-1"), new byte[0]);
            Assert.Empty(backend.Scan());
        }

        [Fact]
        public void A_device_that_never_showed_descriptors_is_still_listed_as_unknown()
        {
            sys.PlugKeyboard();
            File.Delete(Descriptors("1-1"));
            var backend = new LinuxDeviceBackend(sys.Root, () => new HashSet<string>());
            UsbDevice device = Assert.Single(backend.Scan());
            Assert.False(device.FunctionsKnown);
            Assert.Single(backend.Scan());
        }

        [Fact]
        public void The_next_device_in_the_same_port_starts_afresh()
        {
            sys.PlugKeyboard();
            var backend = new LinuxDeviceBackend(sys.Root, () => new HashSet<string>());
            Assert.Single(backend.Scan());
            sys.Unplug("1-1");
            Assert.Empty(backend.Scan());

            // Something that shows no descriptors goes into the same port. It is not the keyboard leaving.
            sys.PlugKeyboard();
            File.Delete(Descriptors("1-1"));
            UsbDevice device = Assert.Single(backend.Scan());
            Assert.False(device.FunctionsKnown);
        }

        [Fact]
        public void Under_trusted_only_a_keyboard_that_is_being_pulled_out_is_not_taken_for_an_unknown_device()
        {
            sys.PlugKeyboard();
            worker = new GuardWorker(new GuardOptions
            {
                Platform = new LinuxPlatform(sys.Root, Path.Combine(folder, "99-lotus-readonly.rules")),
                PipeName = socket,
                AdministratorsOnly = false
            });
            worker.Start();
            Assert.True(worker.WaitUntilReady(10000), "the service loop did not come up");
            var client = new UnixControlClient(socket);
            Assert.True(client.Apply(new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block }).Ok);
            Thread.Sleep(500);
            Assert.Equal("1", sys.Authorized("1-1"));

            // The first step of the kernel taking it away. The folder stays for the test to look at.
            File.Delete(Descriptors("1-1"));
            worker.Kick();
            Thread.Sleep(4500);

            Assert.Equal("1", sys.Authorized("1-1"));
            List<LotusEvent> events = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile).ReadRecent(1000);
            Assert.DoesNotContain(events, e => e.Type == EventTypes.Blocked);
            Assert.DoesNotContain(events, e => e.Type == EventTypes.Warning);
        }
    }

    /// <summary>
    /// A kernel started with usbcore.authorized_default=2 holds back every
    /// device that is not built in, and LOTUS has to take over from there.
    /// </summary>
    public class KernelHoldGateTests : IDisposable
    {
        private readonly FakeSysfs sys = new FakeSysfs();
        private readonly string saved;
        private readonly string kernel;
        private static readonly InstallPlan Plan = new InstallPlan(new List<string> { "x" }, new List<string>());

        public KernelHoldGateTests()
        {
            saved = Path.Combine(sys.Root, "..", Path.GetFileName(sys.Root) + ".saved");
            kernel = Path.Combine(sys.Root, "..", Path.GetFileName(sys.Root) + ".kernel");
        }

        public void Dispose()
        {
            if (File.Exists(saved)) File.Delete(saved);
            if (File.Exists(kernel)) File.Delete(kernel);
            sys.Dispose();
        }

        private LinuxUsbGate Gate(string kernelValue)
        {
            if (kernelValue != null) File.WriteAllText(kernel, kernelValue + "\n");
            return new LinuxUsbGate(new LinuxDeviceBackend(sys.Root, () => new HashSet<string>()), saved,
                kernelValue != null ? kernel : null);
        }

        private void KernelLeftThemWaiting()
        {
            sys.SetDefault("usb1", "2");
            sys.SetDefault("usb2", "2");
        }

        [Fact]
        public void Controllers_the_kernel_left_waiting_become_LOTUSs_own()
        {
            KernelLeftThemWaiting();
            LinuxUsbGate gate = Gate("2");
            Assert.True(gate.KernelHoldsAtStart);
            Assert.False(gate.Owned);

            gate.TakeOverAtStart();

            Assert.True(gate.Owned);
            Assert.False(gate.Foreign);
            Assert.True(gate.Matches(Plan));
            Assert.Equal("0", sys.Default("usb1"));
            Assert.Equal("0", sys.Default("usb2"));
            Assert.Contains("usbcore.authorized_default=2", gate.StartNote);
            Assert.False(gate.StartNoteIsProblem);
        }

        [Fact]
        public void Letting_go_opens_them_because_nothing_else_would_switch_a_new_device_on()
        {
            KernelLeftThemWaiting();
            LinuxUsbGate gate = Gate("2");
            gate.TakeOverAtStart();
            gate.Remove();

            Assert.False(gate.Owned);
            Assert.Equal("1", sys.Default("usb1"));
            Assert.Equal("1", sys.Default("usb2"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("1")]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("")]
        [InlineData("2 and more")]
        public void Without_that_kernel_setting_a_waiting_controller_is_left_as_it_is(string kernelValue)
        {
            KernelLeftThemWaiting();
            LinuxUsbGate gate = Gate(kernelValue);
            Assert.False(gate.KernelHoldsAtStart);

            gate.TakeOverAtStart();

            Assert.False(gate.Owned);
            Assert.Null(gate.StartNote);
            Assert.Equal("2", sys.Default("usb1"));
        }

        [Fact]
        public void When_the_daemon_starts_again_later_there_is_nothing_to_take_over()
        {
            // The kernel still shows its setting, but the controllers were opened long ago.
            LinuxUsbGate gate = Gate("2");
            gate.TakeOverAtStart();

            Assert.False(gate.Owned);
            Assert.Null(gate.StartNote);
            Assert.Equal("1", sys.Default("usb1"));
        }

        [Fact]
        public void A_controller_that_shows_up_later_is_taken_over_as_well()
        {
            KernelLeftThemWaiting();
            LinuxUsbGate gate = Gate("2");
            gate.TakeOverAtStart();
            gate.Remove();

            sys.Controller("usb3", "2");
            gate.TakeOverAtStart();

            Assert.True(gate.Owned);
            Assert.Equal("0", sys.Default("usb3"));
            Assert.Equal("1", sys.Default("usb1"));
            gate.Remove();
            Assert.Equal("1", sys.Default("usb3"));
        }

        [Fact]
        public void Strict_mode_on_such_a_system_goes_back_to_open_and_not_to_waiting()
        {
            LinuxUsbGate gate = Gate("2");
            // Something set one controller back to the waiting state while the daemon was not looking.
            sys.SetDefault("usb2", "2");
            gate.Apply(Plan);
            Assert.Equal("0", sys.Default("usb2"));
            gate.Remove();
            Assert.Equal("1", sys.Default("usb2"));
        }

        [Fact]
        public void A_note_from_before_the_restart_does_not_bring_the_waiting_state_back()
        {
            // An earlier version of LOTUS wrote down "2" for a controller. After a
            // restart with the kernel setting, going back to 2 would strand every new device.
            File.WriteAllText(saved, "usb1=2\nusb2=1\n");
            KernelLeftThemWaiting();
            LinuxUsbGate gate = Gate("2");
            gate.TakeOverAtStart();
            gate.Remove();
            Assert.Equal("1", sys.Default("usb1"));
            Assert.Equal("1", sys.Default("usb2"));
        }

        [Fact]
        public void A_controller_that_cannot_be_written_is_reported_and_does_not_stop_the_daemon()
        {
            KernelLeftThemWaiting();
            LinuxUsbGate gate = Gate("2");
            // A folder where the file should be makes the write fail on every system.
            string file = Path.Combine(sys.Root, "usb1", "authorized_default");
            string keep = File.ReadAllText(file);
            File.Delete(file);
            Directory.CreateDirectory(file);
            try
            {
                gate.TakeOverAtStart();
            }
            finally
            {
                Directory.Delete(file);
                File.WriteAllText(file, keep);
            }
            // usb1 could not even be read, so only usb2 was waiting and it was taken over.
            Assert.Equal("0", sys.Default("usb2"));
        }
    }

    /// <summary>The same through the real service loop: what was held back at the start is judged, not just let in.</summary>
    public class KernelHoldDaemonTests : IDisposable
    {
        private readonly FakeSysfs sys = new FakeSysfs();
        private readonly string folder = Path.Combine(Path.GetTempPath(), "lotus-hold-" + Guid.NewGuid().ToString("N"));
        private readonly string socket =
            Path.Combine(Path.GetTempPath(), "lotus-h-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".sock");
        private readonly string kernel;
        private GuardWorker worker;

        public KernelHoldDaemonTests()
        {
            LotusPaths.OverrideDataDir(folder);
            kernel = Path.Combine(folder, "kernel-setting");
        }

        public void Dispose()
        {
            worker?.Dispose();
            LotusPaths.OverrideDataDir(null);
            sys.Dispose();
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
                if (File.Exists(socket)) File.Delete(socket);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private UnixControlClient Start()
        {
            worker?.Dispose();
            worker = new GuardWorker(new GuardOptions
            {
                Platform = new LinuxPlatform(sys.Root, Path.Combine(folder, "99-lotus-readonly.rules")) { KernelSettingPath = kernel },
                PipeName = socket,
                AdministratorsOnly = false
            });
            worker.Start();
            Assert.True(worker.WaitUntilReady(10000), "the service loop did not come up");
            return new UnixControlClient(socket);
        }

        /// <summary>The computer starts again with the kernel setting: every controller waits, and so does every device.</summary>
        private void Restart(params string[] devices)
        {
            worker?.Dispose();
            worker = null;
            Directory.CreateDirectory(folder);
            File.WriteAllText(kernel, "2\n");
            sys.SetDefault("usb1", "2");
            sys.SetDefault("usb2", "2");
            foreach (string device in devices) sys.SetAuthorized(device, "0");
        }

        private List<LotusEvent> Events()
        {
            return new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile).ReadRecent(1000);
        }

        private bool Becomes(string device, string value)
        {
            return Wait.Until(() => sys.Authorized(device) == value);
        }

        [Fact]
        public void A_disarmed_LOTUS_switches_on_everything_the_kernel_held_back()
        {
            sys.PlugStick();
            sys.PlugKeyboard();
            Restart("1-2", "1-1");

            UnixControlClient client = Start();

            Assert.True(Becomes("1-1", "1"));
            Assert.True(Becomes("1-2", "1"));
            Assert.True(Wait.Until(() => sys.Default("usb1") == "1" && sys.Default("usb2") == "1"));
            Assert.Contains(Events(), e => e.Message.Contains("usbcore.authorized_default=2"));
            Assert.Equal("off", Json.GetString(client.Read().Data, "strictState", ""));
        }

        [Fact]
        public void A_device_the_policy_blocks_stays_off_and_one_it_allows_is_switched_on()
        {
            UnixControlClient client = Start();
            GuardReply applied = client.Apply(new Policy { Armed = true, Storage = StorageMode.Block });
            Assert.True(applied.Ok, applied.Message);

            // Both are plugged in while the computer is off.
            sys.PlugStick();
            sys.PlugKeyboard();
            Restart("1-2", "1-1");
            Start();

            Assert.True(Becomes("1-1", "1"));
            Assert.True(Wait.Until(() => sys.Default("usb1") == "1"));
            Thread.Sleep(600);
            Assert.Equal("0", sys.Authorized("1-2"));
            Assert.Contains(Events(), e => e.Type == EventTypes.Blocked && e.DeviceId.EndsWith("4C530001230505116353"));
            // Nothing ever switched the stick on, so nothing had to switch it off again.
            Assert.DoesNotContain(Events(), e => e.Type == EventTypes.Released);
        }

        [Fact]
        public void With_strict_mode_the_controllers_stay_shut_and_allowed_devices_are_let_in_one_by_one()
        {
            UnixControlClient client = Start();
            GuardReply applied = client.Apply(new Policy { Armed = true, Storage = StorageMode.Block, Strict = true });
            Assert.True(applied.Ok, applied.Message);
            Assert.True(Wait.Until(() => sys.Default("usb1") == "0"));

            sys.PlugStick();
            sys.PlugKeyboard();
            Restart("1-2", "1-1");
            client = Start();

            Assert.True(Becomes("1-1", "1"));
            Thread.Sleep(600);
            Assert.Equal("0", sys.Authorized("1-2"));
            Assert.Equal("0", sys.Default("usb1"));
            Assert.Equal("0", sys.Default("usb2"));
            Assert.Equal("on", Json.GetString(client.Read().Data, "strictState", ""));

            // Disarming opens the controllers and gives the stick back. It does not go back to waiting.
            Assert.True(client.Apply(new Policy()).Ok);
            Assert.True(Becomes("1-2", "1"));
            Assert.True(Wait.Until(() => sys.Default("usb1") == "1" && sys.Default("usb2") == "1"));
        }

        [Fact]
        public void A_trusted_stick_that_was_held_back_is_switched_on()
        {
            sys.PlugStick();
            UnixControlClient client = Start();
            var policy = new Policy { Armed = true, Storage = StorageMode.Block };
            policy.AddTrust(new TrustEntry { Kind = TrustKind.Device, Id = @"USB\VID_0781&PID_5567\4C530001230505116353", Name = "Stick" });
            Assert.True(client.Apply(policy).Ok);
            Thread.Sleep(300);
            Assert.Equal("1", sys.Authorized("1-2"));

            Restart("1-2");
            Start();

            Assert.True(Becomes("1-2", "1"));
        }

        [Fact]
        public void Without_the_kernel_setting_a_device_someone_switched_off_is_left_alone()
        {
            sys.PlugKeyboard();
            sys.SetAuthorized("1-1", "0");
            Start();
            Thread.Sleep(700);
            Assert.Equal("0", sys.Authorized("1-1"));
        }
    }
}
