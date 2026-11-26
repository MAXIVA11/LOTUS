using System;
using System.Collections.Generic;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    public class ClassifierTests
    {
        private static UsbDevice Classify(string service, string[] ids, params DeviceNodeInfo[] children)
        {
            var device = new UsbDevice();
            UsbClassifier.Classify(device, "USB", service, ids, children);
            return device;
        }

        private static DeviceNodeInfo Node(string id, string cls, params string[] compatible)
        {
            return new DeviceNodeInfo { InstanceId = id, ClassName = cls, CompatibleIds = new List<string>(compatible) };
        }

        [Fact]
        public void A_flash_drive_is_storage()
        {
            UsbDevice d = Classify("USBSTOR",
                new[] { @"USB\VID_0781&PID_5567&REV_0100", @"USB\Class_08&SubClass_06&Prot_50", @"USB\Class_08" },
                Node(@"USBSTOR\DISK&VEN_SANDISK\4C53&0", "DiskDrive"));
            Assert.Equal(DeviceKinds.Storage, d.Kinds);
            Assert.True(d.FunctionsKnown);
            Assert.True(d.HasStorage);
            Assert.Equal(DeviceKinds.None, d.NonStorageKinds);
            Assert.Contains("08", d.Functions);
        }

        [Fact]
        public void A_uas_drive_is_storage()
        {
            UsbDevice d = Classify("UASPStor", new[] { @"USB\Class_08&SubClass_06&Prot_62", @"USB\Class_08" });
            Assert.Equal(DeviceKinds.Storage, d.Kinds);
        }

        [Fact]
        public void A_composite_keyboard_is_input()
        {
            UsbDevice d = Classify("usbccgp", new[] { @"USB\VID_1A2C&PID_2D23", @"USB\COMPOSITE" },
                Node(@"USB\VID_1A2C&PID_2D23&MI_00\6&C3A2486&0&0000", "HIDClass", @"USB\Class_03&SubClass_01&Prot_01", @"USB\Class_03"),
                Node(@"HID\VID_1A2C&PID_2D23&MI_00\7&3B19E5D3&0&0000", "Keyboard"),
                Node(@"USB\VID_1A2C&PID_2D23&MI_01\6&C3A2486&0&0001", "HIDClass", @"USB\Class_03"));
            Assert.True(d.IsComposite);
            Assert.True(d.FunctionsKnown);
            Assert.Equal(DeviceKinds.Input, d.Kinds);
        }

        [Fact]
        public void A_composite_device_with_no_interfaces_yet_is_unknown()
        {
            UsbDevice d = Classify("usbccgp", new[] { @"USB\COMPOSITE" });
            Assert.True(d.IsComposite);
            Assert.False(d.FunctionsKnown);
            Assert.Equal(DeviceKinds.None, d.Kinds);
        }

        [Fact]
        public void A_headset_is_audio_and_input()
        {
            UsbDevice d = Classify("usbccgp", new[] { @"USB\COMPOSITE" },
                Node(@"USB\VID_03F0&PID_0C9D&MI_00\6&1&0&0000", "MEDIA", @"USB\Class_01&SubClass_01&Prot_00", @"USB\Class_01"),
                Node(@"USB\VID_03F0&PID_0C9D&MI_03\6&1&0&0003", "HIDClass", @"USB\Class_03"));
            Assert.Equal(DeviceKinds.Audio | DeviceKinds.Input, d.Kinds);
        }

        [Fact]
        public void A_hub_is_only_a_hub()
        {
            UsbDevice d = Classify("USBHUB3", new[] { @"USB\USB30_HUB", @"USB\Class_09&SubClass_00&Prot_00", @"USB\Class_09" });
            Assert.True(d.IsHub);
            Assert.Equal(DeviceKinds.Hub, d.Kinds);
            Assert.True(d.FunctionsKnown);
        }

        [Fact]
        public void A_bluetooth_adapter_is_bluetooth()
        {
            UsbDevice d = Classify("BTHUSB",
                new[] { @"USB\Class_E0&SubClass_01&Prot_01", @"USB\Class_E0&SubClass_01", @"USB\Class_E0" });
            Assert.Equal(DeviceKinds.Bluetooth, d.Kinds);
        }

        [Fact]
        public void Devices_paired_with_a_bluetooth_adapter_do_not_change_what_the_adapter_is()
        {
            UsbDevice d = Classify("BTHUSB",
                new[] { @"USB\Class_E0&SubClass_01&Prot_01", @"USB\Class_E0&SubClass_01", @"USB\Class_E0" },
                Node(@"BTH\MS_BTHPAN\7&1&0&2", "Net"),
                Node(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D\8&1&0&C4", "HIDClass"),
                Node(@"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D\9&1&0&0000", "Keyboard"),
                Node(@"BTHENUM\DEV_A1B2C3D4E5F6\8&1&0&BLUETOOTHDEVICE", "WPD"),
                Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{GUID}", "AudioEndpoint"));
            Assert.Equal(DeviceKinds.Bluetooth, d.Kinds);
            Assert.False(d.HasStorage);
        }

        [Fact]
        public void A_disk_behind_a_vendor_specific_interface_still_counts_as_storage()
        {
            UsbDevice d = Classify("CustomStor", new[] { @"USB\Class_FF&SubClass_00&Prot_00" },
                Node(@"SCSI\DISK&VEN_ACME&PROD_VAULT\7&1&0&000000", "DiskDrive"));
            Assert.True(d.HasStorage);
        }

        [Fact]
        public void A_wifi_adapter_with_a_vendor_specific_class_is_network()
        {
            var device = new UsbDevice();
            UsbClassifier.Classify(device, "Net", "rtwlanu", new[] { @"USB\Class_FF&SubClass_FF&Prot_FF" }, null);
            Assert.True((device.Kinds & DeviceKinds.Network) != 0);
        }

        [Fact]
        public void A_phone_in_file_transfer_mode_counts_as_storage()
        {
            UsbDevice d = Classify("WUDFWpdMtp", new[] { @"USB\MS_COMP_MTP", @"USB\Class_FF&SubClass_FF&Prot_00" });
            Assert.True((d.Kinds & DeviceKinds.Portable) != 0);
            Assert.True(d.HasStorage);
            Assert.Contains("MTP", d.Functions);
        }

        [Fact]
        public void A_camera_using_ptp_counts_as_storage()
        {
            UsbDevice d = Classify("WUDFWpdMtp", new[] { @"USB\Class_06&SubClass_01&Prot_01", @"USB\Class_06" });
            Assert.Equal(DeviceKinds.Portable, d.Kinds);
        }

        [Fact]
        public void A_stick_that_also_claims_to_be_a_keyboard_is_both()
        {
            UsbDevice d = Classify("usbccgp", new[] { @"USB\COMPOSITE" },
                Node(@"USB\VID_1234&PID_5678&MI_00\7&1&0&0000", "USB", @"USB\Class_08&SubClass_06&Prot_50"),
                Node(@"USB\VID_1234&PID_5678&MI_01\7&1&0&0001", "HIDClass", @"USB\Class_03&SubClass_01&Prot_01"));
            Assert.Equal(DeviceKinds.Storage | DeviceKinds.Input, d.Kinds);
            Assert.Equal(DeviceKinds.Input, d.NonStorageKinds);
        }

        [Fact]
        public void A_vendor_specific_device_is_other()
        {
            UsbDevice d = Classify("WinUSB", new[] { @"USB\Class_FF&SubClass_00&Prot_00", @"USB\Class_FF" });
            Assert.Equal(DeviceKinds.Other, d.Kinds);
        }

        [Fact]
        public void A_device_that_failed_to_identify_is_other()
        {
            UsbDevice d = Classify("", new[] { @"USB\DEVICE_DESCRIPTOR_FAILURE" });
            Assert.True(d.FunctionsKnown);
            Assert.Equal(DeviceKinds.Other, d.Kinds);
        }

        [Theory]
        [InlineData(@"USB\VID_0781&PID_5567\4C530001230505116353", "0781", "5567", "4C530001230505116353", true)]
        [InlineData(@"usb\vid_1a2c&pid_2d23\5&cc3f949&0&6", "1A2C", "2D23", "5&CC3F949&0&6", false)]
        public void Splits_instance_ids(string id, string vid, string pid, string tail, bool serial)
        {
            Assert.True(UsbClassifier.ParseInstanceId(id, out string v, out string p, out string t));
            Assert.Equal(vid, v);
            Assert.Equal(pid, p);
            Assert.Equal(tail, t);
            Assert.Equal(serial, UsbClassifier.LooksLikeSerial(t));
        }

        [Theory]
        [InlineData(@"USB\ROOT_HUB30\4&2FD48294&0&0")]
        [InlineData(@"HID\VID_046D&PID_C542\6&1&0&0000")]
        [InlineData("")]
        public void Ignores_ids_that_are_not_usb_devices(string id)
        {
            Assert.False(UsbClassifier.ParseInstanceId(id, out string _, out string _, out string _));
        }
    }

    public class PolicyEngineTests
    {
        private static readonly TimeSpan Settled = TimeSpan.FromMinutes(1);

        private static Policy Armed(StorageMode storage, PnpMode pnp, bool allowInput = true)
        {
            return new Policy { Armed = true, Storage = storage, Pnp = pnp, AllowInput = allowInput };
        }

        private static Decision Decide(Policy policy, UsbDevice device)
        {
            return PolicyEngine.Evaluate(policy, device, Settled);
        }

        [Fact]
        public void Disarmed_blocks_nothing_whatever_the_settings()
        {
            var policy = new Policy { Armed = false, Storage = StorageMode.Block, Pnp = PnpMode.Block, AllowInput = false };
            foreach (UsbDevice device in new[] { Devices.Stick(), Devices.Keyboard(), Devices.Phone(), Devices.Hub() })
            {
                Decision d = Decide(policy, device);
                Assert.Equal(Verdict.Allow, d.Verdict);
                Assert.Equal(Reason.Disarmed, d.Reason);
            }
        }

        [Fact]
        public void Armed_with_everything_allowed_blocks_nothing()
        {
            Policy policy = Armed(StorageMode.Allow, PnpMode.Allow);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Stick()).Verdict);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Keyboard()).Verdict);
        }

        [Fact]
        public void Storage_block_stops_drives_and_phones_but_not_other_devices()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            Assert.Equal(Reason.StorageBlocked, Decide(policy, Devices.Stick()).Reason);
            Assert.Equal(Reason.StorageBlocked, Decide(policy, Devices.Phone()).Reason);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Keyboard()).Verdict);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Headset()).Verdict);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Hub()).Verdict);
        }

        [Fact]
        public void Read_only_never_blocks_a_device()
        {
            Policy policy = Armed(StorageMode.ReadOnly, PnpMode.Allow);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Stick()).Verdict);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Phone()).Verdict);
        }

        [Fact]
        public void Pnp_block_stops_untrusted_devices_but_leaves_plain_storage_to_the_storage_rule()
        {
            Policy policy = Armed(StorageMode.Allow, PnpMode.Block);
            Assert.Equal(Reason.PnpBlocked, Decide(policy, Devices.Headset()).Reason);
            Assert.Equal(Reason.PnpBlocked, Decide(policy, Devices.Hub()).Reason);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Stick()).Verdict);
            Assert.Equal(Verdict.Allow, Decide(policy, Devices.Phone()).Verdict);
        }

        [Fact]
        public void Keyboards_and_mice_stay_allowed_by_default()
        {
            Policy policy = Armed(StorageMode.Allow, PnpMode.Block, allowInput: true);
            Assert.Equal(Reason.InputAllowed, Decide(policy, Devices.Keyboard()).Reason);
            Assert.Equal(Reason.InputAllowed, Decide(policy, Devices.Mouse()).Reason);
            // A headset has buttons too, but it is more than an input device.
            Assert.Equal(Reason.PnpBlocked, Decide(policy, Devices.Headset()).Reason);
        }

        [Fact]
        public void Keyboards_are_blocked_once_the_safety_net_is_off()
        {
            Policy policy = Armed(StorageMode.Allow, PnpMode.Block, allowInput: false);
            Assert.Equal(Reason.PnpBlocked, Decide(policy, Devices.Keyboard()).Reason);
            Assert.Equal(Reason.PnpBlocked, Decide(policy, Devices.Mouse()).Reason);
        }

        [Fact]
        public void A_trusted_device_is_never_blocked()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Block, allowInput: false);
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            policy.AddTrust(TrustEntry.ForModel(Devices.Keyboard()));

            Decision stick = Decide(policy, Devices.Stick());
            Assert.Equal(Reason.Trusted, stick.Reason);
            Assert.Equal(TrustKind.Device, stick.Trust.Kind);
            Assert.Equal(Reason.Trusted, Decide(policy, Devices.Keyboard()).Reason);
            Assert.Equal(Reason.StorageBlocked,
                Decide(policy, Devices.Stick(@"USB\VID_0781&PID_5567\SOMEONE_ELSES")).Reason);
        }

        [Fact]
        public void Built_in_devices_are_exempt_from_the_pnp_rule_only()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Block, allowInput: false);
            Assert.Equal(Reason.BuiltIn, Decide(policy, Devices.BuiltInWebcam()).Reason);

            UsbDevice cardReader = Devices.Stick(@"USB\VID_0BDA&PID_0129\20100201396000000");
            cardReader.BuiltIn = true;
            Assert.Equal(Reason.StorageBlocked, Decide(policy, cardReader).Reason);
        }

        [Fact]
        public void The_system_disk_is_never_blocked()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Block, allowInput: false);
            UsbDevice disk = Devices.Stick();
            disk.IsSystemDisk = true;
            Assert.Equal(Reason.SystemDisk, Decide(policy, disk).Reason);
        }

        [Fact]
        public void A_storage_device_with_a_hidden_keyboard_does_not_slip_through()
        {
            UsbDevice combo = Devices.StorageKeyboardCombo();
            // Storage allowed, PnP blocked, keyboards exempt: the input part is exempt
            // and the storage part is allowed, so the device works.
            Assert.Equal(Verdict.Allow, Decide(Armed(StorageMode.Allow, PnpMode.Block), combo).Verdict);
            // With the keyboard safety net off, the hidden keyboard gets it blocked.
            Assert.Equal(Reason.PnpBlocked, Decide(Armed(StorageMode.Allow, PnpMode.Block, false), combo).Reason);
            // Storage blocked: blocked as storage, whatever else it does.
            Assert.Equal(Reason.StorageBlocked, Decide(Armed(StorageMode.Block, PnpMode.Allow), combo).Reason);
        }

        [Fact]
        public void A_fresh_composite_device_gets_a_moment_to_identify_itself()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            UsbDevice fresh = Devices.FreshComposite();

            Decision early = PolicyEngine.Evaluate(policy, fresh, TimeSpan.FromMilliseconds(200));
            Assert.Equal(Verdict.Pending, early.Verdict);

            // Under a storage-only rule an unidentified device is not storage.
            Decision late = PolicyEngine.Evaluate(policy, fresh, PolicyEngine.IdentifyGrace);
            Assert.Equal(Verdict.Allow, late.Verdict);

            // Under the PnP rule a device that never identifies itself is blocked.
            Decision lockdown = PolicyEngine.Evaluate(Armed(StorageMode.Allow, PnpMode.Block), fresh,
                PolicyEngine.IdentifyGrace);
            Assert.Equal(Reason.PnpBlocked, lockdown.Reason);
        }

        [Fact]
        public void An_unidentified_device_is_not_kept_waiting_when_nothing_could_block_it()
        {
            Decision d = PolicyEngine.Evaluate(Armed(StorageMode.ReadOnly, PnpMode.Allow), Devices.FreshComposite(),
                TimeSpan.Zero);
            Assert.Equal(Verdict.Allow, d.Verdict);
        }

        [Fact]
        public void Every_reason_has_an_explanation()
        {
            foreach (Reason reason in Enum.GetValues(typeof(Reason)))
            {
                string text = PolicyEngine.Explain(new Decision { Reason = reason }, new Policy());
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
        }
    }
}
