using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Lotus.Core;
using Xunit;

namespace Lotus.Linux.Tests
{
    public class LinuxDeviceBackendTests : IDisposable
    {
        private readonly FakeSysfs sys = new FakeSysfs();

        public void Dispose()
        {
            sys.Dispose();
        }

        private LinuxDeviceBackend Backend(Func<HashSet<string>> systemDevices = null)
        {
            return new LinuxDeviceBackend(sys.Root, systemDevices);
        }

        [Fact]
        public void A_device_is_read_from_what_the_kernel_shows()
        {
            sys.PlugStick();
            UsbDevice stick = Assert.Single(Backend().Scan());
            Assert.Equal(@"USB\VID_0781&PID_5567\4C530001230505116353", stick.InstanceId);
            Assert.Equal("Cruzer Blade", stick.Name);
            Assert.Equal("SanDisk", stick.Vendor);
            Assert.Equal("0781", stick.Vid);
            Assert.Equal("5567", stick.Pid);
            Assert.True(stick.HasSerial);
            Assert.Equal(new[] { "08" }, stick.Functions);
            Assert.Equal(DeviceKinds.Storage, stick.Kinds);
            Assert.True(stick.FunctionsKnown);
            Assert.True(stick.Described);
            Assert.True(stick.Started);
            Assert.False(stick.BuiltIn);
            Assert.False(stick.IsComposite);
            Assert.Equal("1-2", stick.Location);
        }

        [Fact]
        public void Controllers_and_functions_are_not_listed_as_devices()
        {
            sys.PlugStick();
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(Path.Combine(sys.Root, "1-2:1.1"));
                Directory.CreateDirectory(Path.Combine(sys.Root, "1-0:1.0"));
            }
            Assert.Single(Backend().Scan());
            Assert.Equal(new[] { "usb1", "usb2" }, Backend().Controllers());
        }

        [Theory]
        [InlineData("1-1", true)]
        [InlineData("12-3.4.10", true)]
        [InlineData("usb1", false)]
        [InlineData("1-1:1.0", false)]
        [InlineData("1-", false)]
        [InlineData("-1", false)]
        [InlineData("1-1..2", false)]
        [InlineData("a-1", false)]
        [InlineData("", false)]
        public void Device_names_are_told_from_everything_else(string name, bool device)
        {
            Assert.Equal(device, LinuxDeviceBackend.IsDeviceName(name));
        }

        [Fact]
        public void A_device_without_a_serial_number_is_known_by_its_port()
        {
            sys.PlugKeyboard("1-1");
            UsbDevice keyboard = Assert.Single(Backend().Scan());
            Assert.Equal(@"USB\VID_046D&PID_C31C\P&1-1", keyboard.InstanceId);
            Assert.False(keyboard.HasSerial);
            Assert.Equal("", keyboard.Serial);
            Assert.Equal(DeviceKinds.Input, keyboard.Kinds);
        }

        [Fact]
        public void Two_devices_that_show_the_same_serial_number_do_not_share_an_ID()
        {
            sys.PlugStick("1-2", "SAME");
            sys.PlugStick("2-1", "SAME");
            List<UsbDevice> devices = Backend().Scan();
            Assert.Equal(2, devices.Count);
            Assert.Contains(devices, d => d.InstanceId == @"USB\VID_0781&PID_5567\SAME" && d.Location == "1-2");
            Assert.Contains(devices, d => d.InstanceId == @"USB\VID_0781&PID_5567\P&2-1" && !d.HasSerial);
        }

        [Theory]
        [InlineData("abc123", "ABC123")]
        [InlineData("  ab cd\t", "AB_CD")]
        [InlineData(@"..\..\etc", @".._.._ETC")]
        [InlineData("P&1-2", "P_1-2")] // cannot pass for an ID made up from the port
        [InlineData("___", "")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void A_serial_number_is_cut_down_to_harmless_characters(string serial, string expected)
        {
            Assert.Equal(expected, LinuxDeviceBackend.CleanSerial(serial));
        }

        [Fact]
        public void A_very_long_serial_number_is_cut_short()
        {
            Assert.Equal(64, LinuxDeviceBackend.CleanSerial(new string('7', 500)).Length);
        }

        [Fact]
        public void A_name_from_the_device_cannot_carry_control_characters()
        {
            sys.Plug("1-3", 0x1234, 0x0001, "Nice\u001b[31m drive‮gnp.exe", null, new[] { FakeSysfs.Storage });
            UsbDevice device = Assert.Single(Backend().Scan());
            Assert.DoesNotContain('\u001b', device.Name);
            Assert.DoesNotContain('‮', device.Name);
            Assert.StartsWith("Nice", device.Name);
        }

        [Fact]
        public void A_device_without_a_name_gets_one()
        {
            sys.Plug("1-3", 0x1234, 0x0001, null, null, new[] { FakeSysfs.Storage });
            sys.Plug("1-4", 0x1234, 0x0002, null, null, new[] { FakeSysfs.Storage }, maker: "Acme");
            List<UsbDevice> devices = Backend().Scan();
            Assert.Contains(devices, d => d.Name == "USB device");
            Assert.Contains(devices, d => d.Name == "Acme device");
        }

        [Fact]
        public void Every_function_of_a_composite_device_counts()
        {
            sys.Plug("1-3", 0x1234, 0xABCD, "Flash Disk", null, new[] { FakeSysfs.Storage, FakeSysfs.Keyboard });
            UsbDevice device = Assert.Single(Backend().Scan());
            Assert.True(device.IsComposite);
            Assert.Equal(DeviceKinds.Storage | DeviceKinds.Input, device.Kinds);
            Assert.Equal(new[] { "08", "03" }, device.Functions);
        }

        [Fact]
        public void A_function_hidden_in_a_second_configuration_is_found()
        {
            // The device shows a harmless face first and keeps a keyboard for later.
            byte[] descriptors = FakeSysfs.Descriptors(0x1234, 0x0002, 0,
                FakeSysfs.Configuration(FakeSysfs.Storage), FakeSysfs.Configuration(FakeSysfs.Keyboard));
            sys.Plug("1-3", 0x1234, 0x0002, "Two faces", null, null, descriptors: descriptors);
            UsbDevice device = Assert.Single(Backend().Scan());
            Assert.Equal(DeviceKinds.Storage | DeviceKinds.Input, device.Kinds);
        }

        [Fact]
        public void A_hub_is_a_hub_and_a_fixed_port_means_built_in()
        {
            sys.Plug("1-3", 0x05E3, 0x0608, "USB2.0 Hub", null, new[] { FakeSysfs.Hub }, deviceClass: 9);
            sys.Plug("1-4", 0x8087, 0x0029, "Bluetooth Radio", null, new[] { new byte[] { 0xE0, 1, 1 } },
                deviceClass: 0xE0, removable: "fixed");
            List<UsbDevice> devices = Backend().Scan();
            UsbDevice hub = devices.Single(d => d.Location == "1-3");
            Assert.True(hub.IsHub);
            Assert.Equal(DeviceKinds.Hub, hub.Kinds);
            Assert.True(devices.Single(d => d.Location == "1-4").BuiltIn);
            Assert.False(hub.BuiltIn);
        }

        [Fact]
        public void A_device_behind_a_hub_is_a_device_of_its_own()
        {
            sys.Plug("1-3", 0x05E3, 0x0608, "USB2.0 Hub", null, new[] { FakeSysfs.Hub }, deviceClass: 9);
            sys.PlugStick("1-3.2");
            List<UsbDevice> devices = Backend().Scan();
            Assert.Equal(2, devices.Count);
            Assert.Contains(devices, d => d.Location == "1-3.2" && d.HasStorage);
        }

        [Fact]
        public void Without_descriptors_the_class_file_is_used_and_without_both_nothing_is_assumed()
        {
            sys.Plug("1-3", 0x03F0, 0x0001, "Printer", null, null, deviceClass: 7, withDescriptors: false);
            sys.Plug("1-4", 0x03F0, 0x0002, "Mystery", null, null, deviceClass: 0, withDescriptors: false);
            List<UsbDevice> devices = Backend().Scan();
            UsbDevice printer = devices.Single(d => d.Location == "1-3");
            Assert.Equal(DeviceKinds.Printer, printer.Kinds);
            Assert.True(printer.FunctionsKnown);
            UsbDevice mystery = devices.Single(d => d.Location == "1-4");
            Assert.False(mystery.FunctionsKnown);
            Assert.Equal(DeviceKinds.None, mystery.Kinds);
        }

        [Fact]
        public void Broken_descriptors_do_not_stop_the_scan()
        {
            var random = new Random(7);
            for (int i = 0; i < 40; i++)
            {
                var garbage = new byte[random.Next(0, 300)];
                random.NextBytes(garbage);
                // Half of them start like a real device descriptor and go wrong later.
                if (i % 2 == 0 && garbage.Length >= 2)
                {
                    garbage[0] = 18;
                    garbage[1] = 1;
                }
                sys.Plug("2-" + (i + 1), 0x1111, i, "Broken " + i, null, null, descriptors: garbage);
            }
            sys.PlugStick();
            List<UsbDevice> devices = Backend().Scan();
            Assert.Equal(41, devices.Count);
            Assert.Contains(devices, d => d.Location == "1-2" && d.Kinds == DeviceKinds.Storage);
        }

        [Fact]
        public void A_folder_that_is_not_a_device_is_skipped()
        {
            Directory.CreateDirectory(Path.Combine(sys.Root, "1-5"));
            File.WriteAllText(Path.Combine(sys.Root, "1-5", "idVendor"), "zzzz\n");
            sys.PlugStick();
            Assert.Single(Backend().Scan());
        }

        [Fact]
        public void A_missing_device_tree_is_an_empty_list()
        {
            var backend = new LinuxDeviceBackend(Path.Combine(sys.Root, "nothing-here"), () => new HashSet<string>());
            Assert.Empty(backend.Scan());
            Assert.Empty(backend.Controllers());
        }

        [Fact]
        public void A_device_that_is_off_counts_as_blocked_only_when_LOTUS_did_it()
        {
            sys.PlugStick();
            sys.SetAuthorized("1-2", "0");
            LinuxDeviceBackend backend = Backend();

            UsbDevice waiting = Assert.Single(backend.Scan());
            Assert.False(waiting.Started);
            Assert.True(waiting.InstallRefused);
            Assert.False(waiting.IsOff);
            // What it is stays known while it is off.
            Assert.Equal(DeviceKinds.Storage, waiting.Kinds);

            backend.IsBlockedByLotus = id => id == waiting.InstanceId;
            UsbDevice blocked = Assert.Single(backend.Scan());
            Assert.True(blocked.IsOff);
            Assert.False(blocked.InstallRefused);
        }

        [Fact]
        public void Blocking_and_unblocking_flip_the_kernels_switch()
        {
            sys.PlugStick();
            LinuxDeviceBackend backend = Backend();
            string id = backend.Scan()[0].InstanceId;

            Assert.True(backend.Block(id, out BlockMethod method).Ok);
            Assert.Equal(BlockMethod.Disabled, method);
            Assert.Equal("0", sys.Authorized("1-2"));

            Assert.True(backend.Unblock(id).Ok);
            Assert.Equal("1", sys.Authorized("1-2"));

            sys.SetAuthorized("1-2", "0");
            Assert.True(backend.Reinstall(id).Ok);
            Assert.Equal("1", sys.Authorized("1-2"));
        }

        [Fact]
        public void A_device_that_is_gone_cannot_be_blocked_and_needs_no_unblocking()
        {
            sys.PlugStick();
            LinuxDeviceBackend backend = Backend();
            string id = backend.Scan()[0].InstanceId;
            sys.Unplug("1-2");

            Assert.False(backend.Block(id, out _).Ok);
            DeviceOpResult unblock = backend.Unblock(id);
            Assert.True(unblock.Ok);
            Assert.True(unblock.Gone);
            Assert.Null(backend.ScanOne(id));
            Assert.Empty(backend.ScanAbsent());
            Assert.False(backend.BlockAbsent(id).Ok);
        }

        [Fact]
        public void A_device_is_found_again_after_it_moves_to_another_port()
        {
            sys.PlugStick("1-2");
            LinuxDeviceBackend backend = Backend();
            string id = backend.Scan()[0].InstanceId;
            sys.Unplug("1-2");
            sys.PlugStick("2-4");

            Assert.True(backend.Block(id, out _).Ok);
            Assert.Equal("0", sys.Authorized("2-4"));
        }

        [Fact]
        public void The_devices_the_system_runs_from_are_marked()
        {
            sys.PlugStick("1-2");
            sys.PlugStick("2-1", "OTHER");
            List<UsbDevice> devices = Backend(() => new HashSet<string> { "1-2" }).Scan();
            Assert.True(devices.Single(d => d.Location == "1-2").IsSystemDisk);
            Assert.False(devices.Single(d => d.Location == "2-1").IsSystemDisk);
        }

        [Fact]
        public void When_the_system_disk_cannot_be_worked_out_no_storage_device_is_touched()
        {
            sys.PlugStick();
            sys.PlugKeyboard();
            LinuxDeviceBackend backend = Backend(() => throw new IOException("mount table unreadable"));
            List<UsbDevice> devices = backend.Scan();
            Assert.Equal("mount table unreadable", backend.SystemDiskError);
            Assert.True(devices.Single(d => d.HasStorage).IsSystemDisk);
            Assert.False(devices.Single(d => !d.HasStorage).IsSystemDisk);
        }
    }

    public class LinuxUsbGateTests : IDisposable
    {
        private readonly FakeSysfs sys = new FakeSysfs();
        private readonly string saved;
        private readonly LinuxUsbGate gate;
        private static readonly InstallPlan Plan = new InstallPlan(new List<string> { "x" }, new List<string>());

        public LinuxUsbGateTests()
        {
            saved = Path.Combine(sys.Root, "..", Path.GetFileName(sys.Root) + ".saved");
            gate = new LinuxUsbGate(new LinuxDeviceBackend(sys.Root, () => new HashSet<string>()), saved);
        }

        public void Dispose()
        {
            if (File.Exists(saved)) File.Delete(saved);
            sys.Dispose();
        }

        [Fact]
        public void A_fresh_system_is_supported_and_untouched()
        {
            Assert.True(gate.Supported);
            Assert.False(gate.Owned);
            Assert.False(gate.Foreign);
            Assert.False(gate.Matches(Plan));
        }

        [Fact]
        public void Without_controllers_or_without_the_kernel_switch_strict_mode_is_not_offered()
        {
            File.Delete(Path.Combine(sys.Root, "usb2", "authorized_default"));
            Assert.False(gate.Supported);

            using (var empty = new FakeSysfs(0))
            {
                var none = new LinuxUsbGate(new LinuxDeviceBackend(empty.Root, () => new HashSet<string>()), saved);
                Assert.False(none.Supported);
            }
        }

        [Fact]
        public void Applying_shuts_every_controller_and_remembers_what_was_there()
        {
            sys.SetDefault("usb2", "2");
            gate.Apply(Plan);
            Assert.Equal("0", sys.Default("usb1"));
            Assert.Equal("0", sys.Default("usb2"));
            Assert.True(gate.Owned);
            Assert.True(gate.Matches(Plan));
            Assert.False(gate.Foreign);

            gate.Remove();
            Assert.Equal("1", sys.Default("usb1"));
            Assert.Equal("2", sys.Default("usb2"));
            Assert.False(gate.Owned);
            Assert.False(File.Exists(saved));
        }

        [Fact]
        public void A_controller_that_appears_later_is_noticed_and_shut_too()
        {
            gate.Apply(Plan);
            sys.Controller("usb3");
            Assert.False(gate.Matches(Plan));
            gate.Apply(Plan);
            Assert.Equal("0", sys.Default("usb3"));
            Assert.True(gate.Matches(Plan));

            gate.Remove();
            Assert.Equal("1", sys.Default("usb3"));
        }

        [Fact]
        public void After_a_restart_of_the_computer_the_controllers_are_shut_again_and_the_old_values_survive()
        {
            sys.SetDefault("usb2", "2");
            gate.Apply(Plan);
            // The kernel forgets the setting when the computer restarts. The note on disk does not.
            sys.SetDefault("usb1", "1");
            sys.SetDefault("usb2", "2");

            Assert.True(gate.Owned);
            Assert.False(gate.Matches(Plan));
            gate.Apply(Plan);
            Assert.Equal("0", sys.Default("usb1"));
            Assert.Equal("0", sys.Default("usb2"));

            gate.Remove();
            Assert.Equal("1", sys.Default("usb1"));
            Assert.Equal("2", sys.Default("usb2"));
        }

        [Fact]
        public void A_controller_someone_else_shut_is_foreign_and_left_alone()
        {
            sys.SetDefault("usb1", "0");
            Assert.True(gate.Foreign);
            Assert.Throws<InvalidOperationException>(() => gate.Apply(Plan));
            Assert.Equal("1", sys.Default("usb2"));
            Assert.False(gate.Owned);

            // Removing what LOTUS never set changes nothing.
            gate.Remove();
            Assert.Equal("0", sys.Default("usb1"));
        }

        [Fact]
        public void LOTUS_never_leaves_a_controller_shut_when_it_lets_go()
        {
            gate.Apply(Plan);
            // Someone edits the note so that it claims the controllers were shut before.
            File.WriteAllText(saved, "usb1=0\nusb2=banana\nusb9=1\n");
            gate.Remove();
            Assert.Equal("1", sys.Default("usb1"));
            Assert.Equal("1", sys.Default("usb2"));
        }

        [Fact]
        public void Naming_an_exception_does_nothing_because_devices_are_switched_on_one_by_one()
        {
            gate.Apply(Plan);
            gate.AllowInstance(@"USB\VID_0781&PID_5567\X");
            Assert.Equal("0", sys.Default("usb1"));
        }
    }

    public class LinuxReadOnlyTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "lotus-ro-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        [Fact]
        public void The_rule_is_a_file_of_its_own_that_comes_and_goes()
        {
            string rule = Path.Combine(folder, "rules.d", "99-lotus-readonly.rules");
            int reloads = 0;
            var readOnly = new LinuxReadOnly(rule, () => reloads++);
            Assert.False(readOnly.IsApplied());

            readOnly.Apply();
            Assert.True(readOnly.IsApplied());
            string text = File.ReadAllText(rule);
            Assert.Contains("SUBSYSTEM==\"block\"", text);
            Assert.Contains("ENV{ID_BUS}==\"usb\"", text);
            Assert.Contains("blockdev --setro /dev/%k", text);
            Assert.Equal(1, reloads);

            readOnly.Remove();
            Assert.False(File.Exists(rule));
            Assert.False(readOnly.IsApplied());
            Assert.Equal(2, reloads);
        }

        [Fact]
        public void A_rule_file_someone_changed_does_not_count_as_applied()
        {
            string rule = Path.Combine(folder, "99-lotus-readonly.rules");
            var readOnly = new LinuxReadOnly(rule, null);
            readOnly.Apply();
            File.WriteAllText(rule, "# emptied\n");
            Assert.False(readOnly.IsApplied());
            readOnly.Apply();
            Assert.True(readOnly.IsApplied());
        }
    }

    public class UeventWatcherTests
    {
        private static bool? Parse(string message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            return UeventWatcher.Parse(bytes, bytes.Length);
        }

        [Fact]
        public void A_whole_usb_device_coming_or_going_is_reported()
        {
            Assert.True(Parse("add@/devices/pci0000:00/usb1/1-2\0ACTION=add\0SUBSYSTEM=usb\0DEVTYPE=usb_device\0"));
            Assert.True(Parse("bind@/devices/x\0ACTION=bind\0SUBSYSTEM=usb\0DEVTYPE=usb_device\0"));
            Assert.False(Parse("remove@/devices/x\0ACTION=remove\0SUBSYSTEM=usb\0DEVTYPE=usb_device\0"));
        }

        [Fact]
        public void Everything_else_is_ignored()
        {
            Assert.Null(Parse("add@/devices/x\0ACTION=add\0SUBSYSTEM=usb\0DEVTYPE=usb_interface\0"));
            Assert.Null(Parse("add@/devices/x\0ACTION=add\0SUBSYSTEM=block\0DEVTYPE=disk\0"));
            Assert.Null(Parse("unbind@/devices/x\0ACTION=unbind\0SUBSYSTEM=usb\0DEVTYPE=usb_device\0"));
            Assert.Null(Parse(""));
            Assert.Null(Parse("\0\0\0garbage=\0=\0"));
        }

        [Fact]
        public void On_a_made_up_tree_the_list_is_watched_instead()
        {
            using (var sys = new FakeSysfs())
            {
                var seen = new List<bool>();
                using (var watcher = new UeventWatcher(arrived => { lock (seen) seen.Add(arrived); }, sys.Root))
                {
                    watcher.Start();
                    Assert.False(watcher.UsesNetlink);
                    Thread.Sleep(400);
                    sys.PlugStick();
                    Assert.True(Wait.Until(() => { lock (seen) return seen.Contains(true); }));
                    sys.Unplug("1-2");
                    Assert.True(Wait.Until(() => { lock (seen) return seen.Contains(false); }));
                }
            }
        }
    }

    internal static class Wait
    {
        public static bool Until(Func<bool> condition, int milliseconds = 8000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(20);
            }
            return condition();
        }
    }

    public class LinuxSystemDiskTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "lotus-disk-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private string Device(string path)
        {
            string full = Path.Combine(root, "sys", "devices", path);
            Directory.CreateDirectory(full);
            return full;
        }

        private void Link(string number, string target)
        {
            string dir = Path.Combine(root, "sys", "dev", "block");
            Directory.CreateDirectory(dir);
            Directory.CreateSymbolicLink(Path.Combine(dir, number), target);
        }

        private string Mounts(params string[] lines)
        {
            string path = Path.Combine(root, "mountinfo");
            Directory.CreateDirectory(root);
            File.WriteAllLines(path, lines);
            return path;
        }

        [Fact]
        public void A_system_on_an_internal_disk_names_no_usb_device()
        {
            if (OperatingSystem.IsWindows()) return; // needs symbolic links the way Linux has them
            Link("259:2", Device("pci0000:00/0000:00:1d.0/nvme/nvme0/nvme0n1/nvme0n1p2"));
            string mounts = Mounts("25 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw");
            Assert.Empty(LinuxSystemDisk.Find(mounts, Path.Combine(root, "sys")));
        }

        [Fact]
        public void A_system_on_a_usb_disk_names_the_disk_and_the_hub_it_hangs_on()
        {
            if (OperatingSystem.IsWindows()) return;
            Link("8:1", Device("pci0000:00/0000:00:14.0/usb2/2-3/2-3.1/2-3.1:1.0/host4/target4:0:0/4:0:0:0/block/sda/sda1"));
            Link("8:17", Device("pci0000:00/0000:00:14.0/usb1/1-4/1-4:1.0/host5/target5:0:0/5:0:0:0/block/sdb/sdb1"));
            string mounts = Mounts(
                "25 1 8:1 / / rw,relatime shared:1 - ext4 /dev/sda1 rw",
                "90 25 8:17 / /media/user/My\\040Stick rw - vfat /dev/sdb1 rw",
                "30 25 0:26 / /proc rw - proc proc rw");
            HashSet<string> found = LinuxSystemDisk.Find(mounts, Path.Combine(root, "sys"));
            Assert.Equal(new[] { "2-3", "2-3.1" }, found.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void An_encrypted_system_is_followed_down_to_the_usb_disk_under_it()
        {
            if (OperatingSystem.IsWindows()) return;
            string partition = Device("pci0000:00/0000:00:14.0/usb1/1-2/1-2:1.0/host4/target4:0:0/4:0:0:0/block/sda/sda3");
            string mapped = Device("virtual/block/dm-0");
            Directory.CreateDirectory(Path.Combine(mapped, "slaves"));
            Directory.CreateSymbolicLink(Path.Combine(mapped, "slaves", "sda3"), partition);
            Link("253:0", mapped);
            string mounts = Mounts("25 1 253:0 / / rw - ext4 /dev/mapper/root rw");
            Assert.Equal(new[] { "1-2" }, LinuxSystemDisk.Find(mounts, Path.Combine(root, "sys")).ToArray());
        }

        [Fact]
        public void A_live_system_on_a_stick_is_found_by_where_its_medium_is_mounted()
        {
            if (OperatingSystem.IsWindows()) return;
            Link("8:1", Device("pci0000:00/0000:00:14.0/usb1/1-5/1-5:1.0/host4/target4:0:0/4:0:0:0/block/sda/sda1"));
            string mounts = Mounts(
                "25 1 0:28 / / rw - overlay overlay rw",
                "40 25 8:1 / /cdrom ro - iso9660 /dev/sda1 ro");
            Assert.Equal(new[] { "1-5" }, LinuxSystemDisk.Find(mounts, Path.Combine(root, "sys")).ToArray());
        }

        [Fact]
        public void An_unreadable_mount_table_is_an_error_and_not_an_empty_answer()
        {
            Assert.ThrowsAny<IOException>(() => LinuxSystemDisk.Find(Path.Combine(root, "missing"), root));
        }

        [Fact]
        public void On_a_real_Linux_the_real_tables_can_be_read()
        {
            if (!OperatingSystem.IsLinux()) return;
            Assert.NotNull(LinuxSystemDisk.Find());
        }
    }

    public class PlainKeyWrapTests
    {
        [Fact]
        public void A_key_comes_back_as_it_went_in()
        {
            byte[] key = Secrets.RandomBytes(32);
            IKeyWrap wrap = PlatformKeyWrap.Create();
            byte[] wrapped = wrap.Wrap(key);
            Assert.NotEqual(key, wrapped);
            Assert.Equal(key, wrap.Unwrap(wrapped));
        }

        [Fact]
        public void Anything_that_is_not_a_LOTUS_key_is_refused()
        {
            IKeyWrap wrap = PlatformKeyWrap.Create();
            Assert.Throws<FormatException>(() => wrap.Unwrap(new byte[0]));
            Assert.Throws<FormatException>(() => wrap.Unwrap(null));
            Assert.Throws<FormatException>(() => wrap.Unwrap(Encoding.ASCII.GetBytes("LOTUSKEY1\n")));
            Assert.Throws<FormatException>(() => wrap.Unwrap(Secrets.RandomBytes(42)));
        }
    }

    public class UnixControlTests : IDisposable
    {
        private readonly string path =
            Path.Combine(Path.GetTempPath(), "lotus-c-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".sock");

        public void Dispose()
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private UnixControlServer Server(Func<Dictionary<string, object>, Dictionary<string, object>> handler)
        {
            var server = new UnixControlServer(path, handler, null, false);
            server.Start();
            return server;
        }

        private static Dictionary<string, object> Echo(Dictionary<string, object> request)
        {
            return new Dictionary<string, object> { ["ok"] = true, ["message"] = "you said " + Json.GetString(request, "op", "") };
        }

        [Fact]
        public void A_request_gets_its_answer()
        {
            using (Server(Echo))
            {
                GuardReply reply = new UnixControlClient(path).Status();
                Assert.True(reply.Ok);
                Assert.Equal("you said status", reply.Message);
            }
        }

        [Fact]
        public void Many_requests_in_a_row_are_answered()
        {
            using (Server(Echo))
            {
                var client = new UnixControlClient(path);
                for (int i = 0; i < 50; i++) Assert.True(client.Read().Ok);
            }
        }

        [Fact]
        public void Without_a_daemon_the_client_says_so()
        {
            var ex = Assert.Throws<GuardUnavailableException>(() => new UnixControlClient(path).Status());
            Assert.Contains("not answering", ex.Message);
        }

        [Fact]
        public void A_socket_file_left_by_an_earlier_run_is_replaced()
        {
            Server(Echo).Dispose();
            File.WriteAllText(path, "stale");
            using (Server(Echo))
            {
                Assert.True(new UnixControlClient(path).Status().Ok);
            }
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void Nonsense_and_a_failing_handler_do_not_take_the_channel_down()
        {
            int calls = 0;
            using (Server(request =>
                   {
                       if (++calls == 1) throw new InvalidOperationException("boom");
                       return Echo(request);
                   }))
            {
                var client = new UnixControlClient(path);
                Assert.Throws<GuardUnavailableException>(() => client.Status()); // the handler threw

                using (var raw = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                           System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
                {
                    raw.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
                    raw.Send(Encoding.UTF8.GetBytes("this is not json\n"));
                }
                using (var raw = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                           System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
                {
                    raw.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
                    // Longer than any message may be, and never a line break.
                    var flood = new byte[64 * 1024];
                    try
                    {
                        for (int sent = 0; sent < GuardProtocol.MaxMessageBytes + flood.Length * 2; sent += flood.Length)
                        {
                            raw.Send(flood);
                        }
                    }
                    catch (System.Net.Sockets.SocketException)
                    {
                        // The server hung up, which is the point.
                    }
                }

                Assert.True(Wait.Until(() =>
                {
                    try
                    {
                        return client.Status().Ok;
                    }
                    catch (GuardUnavailableException)
                    {
                        return false;
                    }
                }));
            }
        }
    }

    /// <summary>The real service loop with the Linux parts under it, on a made-up device tree.</summary>
    public class LinuxGuardTests : IDisposable
    {
        private readonly FakeSysfs sys = new FakeSysfs();
        private readonly string folder = Path.Combine(Path.GetTempPath(), "lotus-linux-" + Guid.NewGuid().ToString("N"));
        private readonly string socket =
            Path.Combine(Path.GetTempPath(), "lotus-g-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".sock");
        private GuardWorker worker;

        private const string StickId = @"USB\VID_0781&PID_5567\4C530001230505116353";

        public LinuxGuardTests()
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

        private string RulePath => Path.Combine(folder, "99-lotus-readonly.rules");

        private UnixControlClient Start()
        {
            worker?.Dispose();
            worker = new GuardWorker(new GuardOptions
            {
                Platform = new LinuxPlatform(sys.Root, RulePath),
                PipeName = socket,
                AdministratorsOnly = false
            });
            worker.Start();
            Assert.True(worker.WaitUntilReady(10000), "the service loop did not come up");
            return new UnixControlClient(socket);
        }

        private static void Apply(UnixControlClient client, Policy policy)
        {
            GuardReply reply = client.Apply(policy);
            Assert.True(reply.Ok, reply.Message);
        }

        private bool Becomes(string device, string value)
        {
            return Wait.Until(() => sys.Authorized(device) == value);
        }

        private List<LotusEvent> Events()
        {
            return new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile).ReadRecent(1000);
        }

        private static Policy StorageBlocked()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block };
        }

        private static Policy TrustedOnly()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block };
        }

        [Fact]
        public void Nothing_is_touched_while_LOTUS_is_disarmed()
        {
            sys.PlugStick();
            UnixControlClient client = Start();
            Assert.False(client.Status().Armed);
            Thread.Sleep(600);
            Assert.Equal("1", sys.Authorized("1-2"));
            Assert.Equal("1", sys.Default("usb1"));
        }

        [Fact]
        public void Arming_switches_storage_off_and_holds_new_devices_at_the_door()
        {
            sys.PlugStick();
            sys.PlugKeyboard();
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());

            Assert.True(Becomes("1-2", "0"));
            Assert.Equal("1", sys.Authorized("1-1"));
            Assert.True(Wait.Until(() => sys.Default("usb1") == "0" && sys.Default("usb2") == "0"));
            Assert.Contains(Events(), e => e.Message == SystemWords.GateClosed);
            Assert.Equal("on", Json.GetString(client.Read().Data, "strictState", ""));
        }

        [Fact]
        public void A_new_device_the_policy_blocks_never_runs_and_one_it_allows_is_switched_on()
        {
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());
            Assert.True(Wait.Until(() => sys.Default("usb2") == "0"));

            // The kernel holds both back, because the controllers are shut.
            sys.PlugStick("2-1");
            sys.PlugPrinter("2-2");
            Assert.Equal("0", sys.Authorized("2-1"));

            Assert.True(Becomes("2-2", "1"));
            Assert.True(Wait.Until(() => Events().Any(e => e.Type == EventTypes.Blocked && e.DeviceId == StickId)));
            Assert.Equal("0", sys.Authorized("2-1"));

            Ledger ledger = client.Read().ReadLedger();
            Assert.True(ledger.Contains(StickId));
        }

        [Fact]
        public void Trusting_a_blocked_device_switches_it_on()
        {
            sys.PlugStick();
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());
            Assert.True(Becomes("1-2", "0"));

            Policy policy = StorageBlocked();
            UsbDevice stick = new LinuxDeviceBackend(sys.Root, () => new HashSet<string>()).Scan().Single();
            policy.AddTrust(TrustEntry.ForDevice(stick));
            Apply(client, policy);
            Assert.True(Becomes("1-2", "1"));

            // The same stick in another port is still the trusted one.
            sys.Unplug("1-2");
            sys.PlugStick("2-3");
            Assert.True(Becomes("2-3", "1"));

            // Another unit of the model is not.
            sys.PlugStick("2-4", "SOMEONE_ELSES");
            Thread.Sleep(900);
            Assert.Equal("0", sys.Authorized("2-4"));
        }

        [Fact]
        public void Under_trusted_only_a_keyboard_still_works_and_a_printer_does_not()
        {
            UnixControlClient client = Start();
            Apply(client, TrustedOnly());
            Assert.True(Wait.Until(() => sys.Default("usb1") == "0"));

            sys.PlugKeyboard("1-1");
            sys.PlugPrinter("1-3");
            Assert.True(Becomes("1-1", "1"));
            Thread.Sleep(900);
            Assert.Equal("0", sys.Authorized("1-3"));
        }

        [Fact]
        public void A_stick_that_also_types_is_blocked_whatever_it_calls_itself()
        {
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());
            Assert.True(Wait.Until(() => sys.Default("usb1") == "0"));
            sys.Plug("1-4", 0x1234, 0xABCD, "USB Keyboard", null, new[] { FakeSysfs.Keyboard, FakeSysfs.Storage });
            Thread.Sleep(900);
            Assert.Equal("0", sys.Authorized("1-4"));
        }

        [Fact]
        public void Disarming_switches_everything_back_on_and_opens_the_controllers()
        {
            sys.SetDefault("usb2", "2");
            sys.PlugStick();
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());
            Assert.True(Becomes("1-2", "0"));
            Assert.True(Wait.Until(() => sys.Default("usb2") == "0"));
            sys.PlugStick("2-1", "SECOND");
            Assert.True(Wait.Until(() => client.Read().ReadLedger().Count == 2));

            Apply(client, new Policy());
            Assert.True(Becomes("1-2", "1"));
            Assert.True(Becomes("2-1", "1"));
            Assert.True(Wait.Until(() => sys.Default("usb1") == "1" && sys.Default("usb2") == "2"));
            Assert.Contains(Events(), e => e.Message == SystemWords.GateOpened);
        }

        [Fact]
        public void With_strict_mode_off_the_controllers_stay_open_and_devices_are_switched_off_after_they_start()
        {
            UnixControlClient client = Start();
            Policy policy = StorageBlocked();
            policy.Strict = false;
            Apply(client, policy);
            Thread.Sleep(500);
            Assert.Equal("1", sys.Default("usb1"));

            sys.PlugStick();
            Assert.True(Becomes("1-2", "0"));
        }

        [Fact]
        public void Controllers_that_another_program_holds_shut_are_left_alone()
        {
            sys.SetDefault("usb1", "0");
            sys.SetDefault("usb2", "0");
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());

            sys.PlugPrinter("2-2"); // waits for the other program, not for LOTUS
            sys.Plug("1-2", 0x0781, 0x5567, "Cruzer Blade", "RUNNING", new[] { FakeSysfs.Storage }, authorized: "1");
            Assert.True(Becomes("1-2", "0"));
            Assert.True(Wait.Until(() => Events().Any(e => e.Message == SystemWords.GateForeign)));
            Thread.Sleep(600);
            Assert.Equal("0", sys.Authorized("2-2"));
            Assert.Equal("foreign", Json.GetString(client.Read().Data, "strictState", ""));

            Apply(client, new Policy());
            Assert.True(Becomes("1-2", "1"));
            Assert.Equal("0", sys.Default("usb1"));
            Assert.Equal("0", sys.Authorized("2-2"));
        }

        [Fact]
        public void A_restart_of_the_daemon_changes_nothing()
        {
            sys.PlugStick();
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());
            Assert.True(Becomes("1-2", "0"));
            Assert.True(Wait.Until(() => sys.Default("usb1") == "0"));

            worker.Dispose();
            worker = null;
            // Plugged in while nothing was watching.
            sys.PlugPrinter("2-2");
            sys.PlugStick("2-1", "LATECOMER");
            Assert.Equal("0", sys.Authorized("2-2"));
            Assert.Equal("0", sys.Default("usb1")); // stopping the daemon does not open the door

            client = Start();
            Assert.True(client.Status().Armed);
            Assert.True(Becomes("2-2", "1"));
            Thread.Sleep(600);
            Assert.Equal("0", sys.Authorized("1-2"));
            Assert.Equal("0", sys.Authorized("2-1"));
        }

        [Fact]
        public void Read_only_mode_is_a_rule_file_that_goes_away_again()
        {
            sys.PlugStick();
            UnixControlClient client = Start();
            Apply(client, new Policy { Armed = true, Storage = StorageMode.ReadOnly });
            Assert.True(Wait.Until(() => File.Exists(RulePath)));
            Assert.True(Becomes("1-2", "1")); // allowed, only not writable
            Assert.Contains(Events(), e => e.Type == EventTypes.ReadOnlyOn);

            Apply(client, new Policy());
            Assert.True(Wait.Until(() => !File.Exists(RulePath)));
        }

        [Fact]
        public void Removing_LOTUS_leaves_the_system_as_it_was()
        {
            sys.PlugStick();
            UnixControlClient client = Start();
            var stopped = new ManualResetEventSlim(false);
            worker.StopRequested += purpose => stopped.Set();
            Apply(client, new Policy { Armed = true, Storage = StorageMode.ReadOnly, Pnp = PnpMode.Block });
            sys.PlugPrinter("2-2");
            Assert.True(Wait.Until(() => sys.Default("usb2") == "0" && File.Exists(RulePath)));
            Assert.True(Wait.Until(() => client.Read().ReadLedger().Count == 1));

            GuardReply reply = client.Stop(GuardProtocol.PurposeUninstall);
            Assert.True(reply.Ok, reply.Message);
            Assert.True(stopped.Wait(5000));

            Assert.Equal("1", sys.Authorized("1-2"));
            Assert.Equal("1", sys.Authorized("2-2"));
            Assert.Equal("1", sys.Default("usb1"));
            Assert.Equal("1", sys.Default("usb2"));
            Assert.False(File.Exists(RulePath));
            Assert.Empty(Directory.GetFiles(LotusPaths.SecureDir));
        }

        [Fact]
        public void The_store_on_disk_shows_nothing_and_only_root_may_open_it()
        {
            sys.PlugStick();
            UnixControlClient client = Start();
            Policy policy = TrustedOnly();
            policy.AddTrust(TrustEntry.ForDevice(new LinuxDeviceBackend(sys.Root, () => new HashSet<string>()).Scan().Single()));
            Apply(client, policy);
            Assert.True(Wait.Until(() => sys.Default("usb1") == "0"));
            sys.PlugPrinter();
            Assert.True(Wait.Until(() => File.Exists(LotusPaths.LedgerFile)));

            foreach (string file in new[] { LotusPaths.StoreFile, LotusPaths.LedgerFile })
            {
                string text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
                foreach (string word in new[] { "armed", "trusted", "storage", "VID_", "Cruzer", "LaserJet" })
                {
                    Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (OperatingSystem.IsWindows()) return;
            UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            Assert.Equal(owner | UnixFileMode.UserExecute, File.GetUnixFileMode(LotusPaths.SecureDir));
            foreach (string file in Directory.GetFiles(LotusPaths.SecureDir))
            {
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(file) & ~owner);
            }
        }

        [Fact]
        public void A_damaged_store_locks_down_and_a_deleted_one_is_put_back()
        {
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());

            File.Delete(LotusPaths.StoreFile);
            Assert.True(Wait.Until(() => File.Exists(LotusPaths.StoreFile)));
            Assert.True(client.Status().Armed);

            worker.Dispose();
            worker = null;
            foreach (string file in Directory.GetFiles(LotusPaths.SecureDir, "policy.dat*"))
            {
                byte[] bytes = File.ReadAllBytes(file);
                bytes[bytes.Length / 2] ^= 0x40;
                File.WriteAllBytes(file, bytes);
            }
            client = Start();
            Assert.True(client.Status().Lockdown);
            sys.PlugStick();
            Assert.True(Becomes("1-2", "0"));
            Assert.False(client.Apply(new Policy()).Ok); // locked down means locked: no way out over the channel

            // The installer's note is the way out. It is used up when it is read.
            worker.Dispose();
            worker = null;
            string note = Path.Combine(LotusPaths.DataDir, LinuxPlatform.ResetNoteName);
            File.WriteAllText(note, "");
            client = Start();
            Assert.False(client.Status().Lockdown);
            Assert.False(client.Status().Armed);
            Assert.False(File.Exists(note));
            Assert.True(Becomes("1-2", "1"));
        }

        [Fact]
        public void A_reset_note_does_nothing_to_a_store_that_is_in_order()
        {
            UnixControlClient client = Start();
            Apply(client, StorageBlocked());
            worker.Dispose();
            worker = null;
            File.WriteAllText(Path.Combine(LotusPaths.DataDir, LinuxPlatform.ResetNoteName), "");
            client = Start();
            Assert.True(client.Status().Armed);
        }
    }
}
