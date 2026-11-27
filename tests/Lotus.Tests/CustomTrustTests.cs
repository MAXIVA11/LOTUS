using System;
using System.Collections.Generic;
using System.Linq;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    /// <summary>Trust entries someone types in: a vendor, a model or an instance path.</summary>
    public class CustomTrustTests
    {
        private static TrustEntry Parse(TrustKind kind, string text, string name = null)
        {
            Assert.True(TrustEntry.TryParse(kind, text, name, out TrustEntry entry, out string error), error);
            Assert.Null(error);
            return entry;
        }

        private static string Problem(TrustKind kind, string text)
        {
            Assert.False(TrustEntry.TryParse(kind, text, null, out TrustEntry entry, out string error));
            Assert.Null(entry);
            Assert.False(string.IsNullOrWhiteSpace(error));
            return error;
        }

        [Theory]
        [InlineData("046D")]
        [InlineData("046d")]
        [InlineData("  046d ")]
        [InlineData("VID_046D")]
        [InlineData("vid_046d")]
        [InlineData("VID:046D")]
        [InlineData("0x046D")]
        public void A_vendor_can_be_typed_in_several_ways(string text)
        {
            TrustEntry entry = Parse(TrustKind.Vendor, text);
            Assert.Equal(TrustKind.Vendor, entry.Kind);
            Assert.Equal("VID_046D", entry.Id);
            Assert.Empty(entry.Functions);
            Assert.Equal("Every device from vendor 046D", entry.Name);
        }

        [Theory]
        [InlineData("046D:C52B")]
        [InlineData("046d:c52b")]
        [InlineData("046D C52B")]
        [InlineData("046D&C52B")]
        [InlineData("046D/C52B")]
        [InlineData("VID_046D&PID_C52B")]
        [InlineData("vid_046d&pid_c52b")]
        [InlineData(@"USB\VID_046D&PID_C52B")]
        [InlineData("0x046D 0xC52B")]
        public void A_model_can_be_typed_in_several_ways(string text)
        {
            TrustEntry entry = Parse(TrustKind.Model, text);
            Assert.Equal(TrustKind.Model, entry.Kind);
            Assert.Equal("VID_046D&PID_C52B", entry.Id);
            Assert.Equal("Every 046D:C52B", entry.Name);
        }

        [Theory]
        [InlineData(@"USB\VID_0781&PID_5567\4C530001230505116353", @"USB\VID_0781&PID_5567\4C530001230505116353")]
        [InlineData(@"usb\vid_0781&pid_5567\4c530001230505116353", @"USB\VID_0781&PID_5567\4C530001230505116353")]
        [InlineData(@"  USB\VID_1A2C&PID_2D23\5&CC3F949&0&6  ", @"USB\VID_1A2C&PID_2D23\5&CC3F949&0&6")]
        [InlineData(@"USB\VID_046D&PID_C31C\P&1-2", @"USB\VID_046D&PID_C31C\P&1-2")]
        public void A_device_is_named_by_its_instance_path(string text, string id)
        {
            TrustEntry entry = Parse(TrustKind.Device, text);
            Assert.Equal(TrustKind.Device, entry.Kind);
            Assert.Equal(id, entry.Id);
            Assert.Equal(id, entry.Name);
        }

        [Theory]
        [InlineData(TrustKind.Vendor, "")]
        [InlineData(TrustKind.Vendor, "   ")]
        [InlineData(TrustKind.Vendor, null)]
        [InlineData(TrustKind.Vendor, "46D")]
        [InlineData(TrustKind.Vendor, "046DZ")]
        [InlineData(TrustKind.Vendor, "GGGG")]
        [InlineData(TrustKind.Vendor, "046D:C52B")]
        [InlineData(TrustKind.Vendor, "VID_")]
        [InlineData(TrustKind.Model, "046D")]
        [InlineData(TrustKind.Model, "046D:")]
        [InlineData(TrustKind.Model, "046D:C52")]
        [InlineData(TrustKind.Model, "046D:C52B:0001")]
        [InlineData(TrustKind.Model, "046D:XYZW")]
        [InlineData(TrustKind.Model, "PID_C52B&VID_046D")]
        [InlineData(TrustKind.Device, "046D:C52B")]
        [InlineData(TrustKind.Device, @"USB\VID_0781&PID_5567")]
        [InlineData(TrustKind.Device, @"USB\VID_0781&PID_5567\")]
        [InlineData(TrustKind.Device, @"HID\VID_0781&PID_5567\1")]
        [InlineData(TrustKind.Device, @"USB\VID_0781&PID_556\1")]
        [InlineData(TrustKind.Device, @"USB\VID_0781&PID_5567\A\B")]
        [InlineData(TrustKind.Device, @"USB\VID_0781&PID_5567\A B")]
        [InlineData(TrustKind.Device, "USB\\VID_0781&PID_5567\\A\tB")]
        [InlineData(TrustKind.Device, "USB\\VID_0781&PID_5567\\A‮B")]
        [InlineData(TrustKind.Device, @"USBSTOR\DISK&VEN_SANDISK\1")]
        public void What_is_not_an_ID_is_refused_with_a_reason(TrustKind kind, string text)
        {
            Problem(kind, text);
        }

        [Fact]
        public void The_path_of_one_function_of_a_device_is_refused_with_a_hint()
        {
            string problem = Problem(TrustKind.Device, @"USB\VID_03F0&PID_0C9D&MI_03\7&2F1A&0&0003");
            Assert.Contains("one function", problem);
        }

        [Fact]
        public void Something_far_too_long_is_refused()
        {
            Assert.Contains("too long", Problem(TrustKind.Device, @"USB\VID_0781&PID_5567\" + new string('A', 300)));
        }

        [Fact]
        public void A_name_that_was_typed_is_cleaned_and_kept()
        {
            TrustEntry entry = Parse(TrustKind.Vendor, "046D", "  Office‮ keyboards\r\n ");
            Assert.Equal("Office keyboards", entry.Name);
        }

        [Fact]
        public void A_vendor_entry_covers_every_device_of_that_vendor_and_no_other()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block, AllowInput = false };
            Assert.True(policy.AddTrust(Parse(TrustKind.Vendor, "046D")));

            UsbDevice mouse = Devices.Mouse(); // Logitech, 046D
            Decision allowed = PolicyEngine.Evaluate(policy, mouse, TimeSpan.FromMinutes(1));
            Assert.Equal(Verdict.Allow, allowed.Verdict);
            Assert.Equal(Reason.Trusted, allowed.Reason);
            Assert.Equal(TrustKind.Vendor, allowed.Trust.Kind);
            Assert.Equal("Trusted. Every device from this vendor is allowed.", PolicyEngine.Explain(allowed, policy));

            Assert.True(PolicyEngine.Evaluate(policy, Devices.Keyboard(), TimeSpan.FromMinutes(1)).IsBlock);
            Assert.True(PolicyEngine.Evaluate(policy, Devices.Stick(), TimeSpan.FromMinutes(1)).IsBlock);
        }

        [Fact]
        public void A_vendor_entry_covers_storage_of_that_vendor_too()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block };
            policy.AddTrust(Parse(TrustKind.Vendor, "0781"));
            Assert.Equal(Verdict.Allow, PolicyEngine.Evaluate(policy, Devices.Stick(), TimeSpan.FromMinutes(1)).Verdict);
        }

        [Fact]
        public void A_device_that_names_no_vendor_matches_no_vendor_entry()
        {
            var entry = new TrustEntry { Kind = TrustKind.Vendor, Id = "VID_" };
            Assert.False(entry.Matches(new UsbDevice { InstanceId = @"USB\UNKNOWN\1", Vid = "" }));
            Assert.False(Parse(TrustKind.Vendor, "046D").Matches(null));
        }

        [Fact]
        public void A_typed_model_and_a_typed_device_match_like_the_ones_made_from_a_device()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block };
            policy.AddTrust(Parse(TrustKind.Model, "0781:5567"));
            Assert.Equal(Verdict.Allow, PolicyEngine.Evaluate(policy, Devices.Stick(), TimeSpan.FromMinutes(1)).Verdict);
            Assert.Equal(Verdict.Allow,
                PolicyEngine.Evaluate(policy, Devices.Stick(@"USB\VID_0781&PID_5567\ANOTHER"), TimeSpan.FromMinutes(1)).Verdict);

            var one = new Policy { Armed = true, Storage = StorageMode.Block };
            one.AddTrust(Parse(TrustKind.Device, Devices.StickId.ToLowerInvariant()));
            Assert.Equal(Verdict.Allow, PolicyEngine.Evaluate(one, Devices.Stick(), TimeSpan.FromMinutes(1)).Verdict);
            Assert.True(PolicyEngine.Evaluate(one, Devices.Stick(@"USB\VID_0781&PID_5567\ANOTHER"), TimeSpan.FromMinutes(1)).IsBlock);
        }

        [Fact]
        public void A_typed_entry_records_no_functions_so_it_covers_whatever_the_device_does()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block };
            policy.AddTrust(Parse(TrustKind.Device, Devices.ComboId));
            UsbDevice combo = Devices.Stick(Devices.ComboId);
            combo.Functions = new List<string> { "08", "03" };
            combo.Kinds = DeviceKinds.Storage | DeviceKinds.Input;
            Assert.Equal(Verdict.Allow, PolicyEngine.Evaluate(policy, combo, TimeSpan.FromMinutes(1)).Verdict);
        }

        [Fact]
        public void Every_kind_survives_a_round_trip_and_the_same_entry_is_not_added_twice()
        {
            var policy = new Policy();
            Assert.True(policy.AddTrust(Parse(TrustKind.Vendor, "046D", "Logitech")));
            Assert.True(policy.AddTrust(Parse(TrustKind.Model, "046D:C52B")));
            Assert.True(policy.AddTrust(Parse(TrustKind.Device, Devices.StickId)));
            Assert.False(policy.AddTrust(Parse(TrustKind.Vendor, "vid_046d")));
            Assert.False(policy.AddTrust(Parse(TrustKind.Model, "046d c52b")));

            Policy copy = Policy.FromJson(policy.ToJson());
            Assert.Equal(new[] { TrustKind.Vendor, TrustKind.Model, TrustKind.Device }, copy.Trusted.Select(e => e.Kind).ToArray());
            Assert.Equal(new[] { "VID_046D", "VID_046D&PID_C52B", Devices.StickId }, copy.Trusted.Select(e => e.Id).ToArray());
            Assert.Equal("Logitech", copy.Trusted[0].Name);
            Assert.Contains("\"kind\": \"vendor\"", policy.ToJson());

            Assert.True(copy.RemoveTrust(TrustKind.Vendor, "vid_046d"));
            Assert.Equal(2, copy.Trusted.Count);
            Assert.Equal(TrustKind.Vendor, policy.Clone().Trusted[0].Kind);
        }

        [Fact]
        public void A_vendor_entry_does_not_put_an_instance_on_the_allow_list_of_Windows()
        {
            // Only an entry for one device is an ID Windows can be told to let through.
            var bus = new SimulatedBackend();
            var gate = new SimulatedInstallGate();
            bus.Gate = gate;
            var ledger = new Ledger();
            var enforcer = new Enforcer(bus, new SimulatedReadOnlyPolicy(), ledger, _ => { }, _ => { },
                () => new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc), gate);
            var policy = new Policy { Armed = true, Pnp = PnpMode.Block, Strict = true };
            policy.AddTrust(Parse(TrustKind.Vendor, "046D"));
            policy.AddTrust(Parse(TrustKind.Device, Devices.StickId));
            enforcer.RunOnce(policy);
            Assert.Contains(Devices.StickId, gate.Plan.AllowInstances);
            Assert.DoesNotContain("VID_046D", gate.Plan.AllowInstances);
        }
    }

    public class TrustAllTests
    {
        [Fact]
        public void Every_connected_device_goes_on_the_list_once()
        {
            var policy = new Policy();
            var devices = new List<UsbDevice> { Devices.Stick(), Devices.Headset(), Devices.Keyboard(), Devices.Mouse() };

            List<TrustEntry> added = policy.TrustAll(devices);

            Assert.Equal(4, added.Count);
            Assert.All(devices, device => Assert.True(policy.IsTrusted(device)));
            Assert.Empty(policy.TrustAll(devices));
            Assert.Equal(4, policy.Trusted.Count);
        }

        [Fact]
        public void A_device_with_a_serial_number_is_trusted_as_itself_and_one_without_as_its_model()
        {
            var policy = new Policy();
            policy.TrustAll(new[] { Devices.Stick(), Devices.Keyboard() });

            TrustEntry stick = policy.Trusted.Single(e => e.Id == Devices.StickId);
            Assert.Equal(TrustKind.Device, stick.Kind);
            Assert.Equal(new[] { "08" }, stick.Functions);

            // The keyboard has no serial number: in another port it would be another device.
            TrustEntry keyboard = policy.Trusted.Single(e => e.Kind == TrustKind.Model);
            Assert.Equal("VID_1A2C&PID_2D23", keyboard.Id);
        }

        [Fact]
        public void What_is_trusted_already_is_left_as_it_is_and_nothing_else_changes()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.Block };
            policy.AddTrust(TrustEntry.ForModel(Devices.Stick()));

            List<TrustEntry> added = policy.TrustAll(new[] { Devices.Stick(), Devices.Headset(), null });

            Assert.Single(added);
            Assert.Equal(2, policy.Trusted.Count);
            Assert.True(policy.Armed);
            Assert.Equal(StorageMode.Block, policy.Storage);
            Assert.Empty(new Policy().TrustAll(null));
        }
    }
}
