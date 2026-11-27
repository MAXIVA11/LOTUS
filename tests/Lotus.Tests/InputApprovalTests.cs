using System;
using System.Collections.Generic;
using System.Linq;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    /// <summary>"New keyboards and mice wait for approval": what the policy says and how it is stored.</summary>
    public class InputApprovalTests
    {
        private static Policy Approve(PnpMode pnp = PnpMode.Allow, StorageMode storage = StorageMode.Allow)
        {
            return new Policy { Armed = true, Pnp = pnp, Storage = storage, Input = InputRule.Approve };
        }

        private static Decision Judge(Policy policy, UsbDevice device)
        {
            return PolicyEngine.Evaluate(policy, device, TimeSpan.MaxValue);
        }

        [Fact]
        public void A_new_keyboard_waits_also_when_every_other_device_is_allowed()
        {
            Decision decision = Judge(Approve(), Devices.Keyboard());
            Assert.True(decision.IsBlock);
            Assert.Equal(Reason.InputWaiting, decision.Reason);
            Assert.Equal(Reason.InputWaiting, Judge(Approve(), Devices.Mouse()).Reason);
        }

        [Fact]
        public void A_device_that_is_something_else_and_an_input_device_too_waits_as_well()
        {
            // A headset with buttons can type as well as any keyboard.
            Assert.Equal(Reason.InputWaiting, Judge(Approve(), Devices.Headset()).Reason);
            Assert.Equal(Reason.InputWaiting, Judge(Approve(), Devices.StorageKeyboardCombo()).Reason);
        }

        [Fact]
        public void What_has_no_input_function_is_left_to_the_other_rules()
        {
            Assert.Equal(Reason.PolicyAllows, Judge(Approve(), Devices.Stick()).Reason);
            Assert.Equal(Reason.PolicyAllows, Judge(Approve(), Devices.Phone()).Reason);
            Assert.Equal(Reason.StorageBlocked, Judge(Approve(storage: StorageMode.Block), Devices.Stick()).Reason);
            // A device with storage and a keyboard is storage first when storage is blocked.
            Assert.Equal(Reason.StorageBlocked,
                Judge(Approve(storage: StorageMode.Block), Devices.StorageKeyboardCombo()).Reason);
        }

        [Fact]
        public void A_trusted_keyboard_works_and_one_built_into_the_computer_does_too()
        {
            Policy policy = Approve();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Keyboard()));
            Assert.Equal(Reason.Trusted, Judge(policy, Devices.Keyboard()).Reason);
            Assert.Equal(Reason.InputWaiting, Judge(policy, Devices.Mouse()).Reason);

            UsbDevice builtIn = Devices.Keyboard();
            builtIn.BuiltIn = true;
            Assert.False(Judge(Approve(), builtIn).IsBlock);
            Assert.Equal(Reason.BuiltIn, Judge(Approve(PnpMode.Block), builtIn).Reason);
        }

        [Fact]
        public void Under_trusted_only_a_keyboard_waits_and_everything_else_is_blocked_as_before()
        {
            Policy policy = Approve(PnpMode.Block);
            Assert.Equal(Reason.InputWaiting, Judge(policy, Devices.Keyboard()).Reason);
            Assert.Equal(Reason.PnpBlocked, Judge(policy, Devices.Hub()).Reason);
            Assert.Equal(Reason.InputWaiting, Judge(policy, Devices.Headset()).Reason);
        }

        [Fact]
        public void A_disarmed_LOTUS_makes_nothing_wait()
        {
            Policy policy = Approve();
            policy.Armed = false;
            Assert.Equal(Reason.Disarmed, Judge(policy, Devices.Keyboard()).Reason);
        }

        [Fact]
        public void A_device_that_has_not_said_what_it_is_gets_a_moment_and_then_counts_as_unknown()
        {
            UsbDevice fresh = Devices.FreshComposite();
            Assert.Equal(Verdict.Pending, PolicyEngine.Evaluate(Approve(), fresh, TimeSpan.Zero).Verdict);
            // Without a word from the device there is no input function to hold it for.
            Assert.Equal(Verdict.Allow, PolicyEngine.Evaluate(Approve(), fresh, TimeSpan.FromSeconds(10)).Verdict);
        }

        [Fact]
        public void A_trusted_headset_that_grew_a_new_function_waits_and_the_text_says_why()
        {
            Policy policy = Approve();
            UsbDevice once = Devices.Mouse();
            policy.AddTrust(TrustEntry.ForDevice(once));
            UsbDevice now = Devices.Mouse();
            now.Functions = new List<string> { "03", "08" };
            now.Kinds = DeviceKinds.Input | DeviceKinds.Storage;
            Decision decision = Judge(policy, now);
            Assert.Equal(Reason.InputWaiting, decision.Reason);
            Assert.Contains("function it did not have", PolicyEngine.Explain(decision, policy));
        }

        [Fact]
        public void The_reason_is_put_into_words()
        {
            Decision decision = Judge(Approve(), Devices.Keyboard());
            Assert.Contains("nobody has approved it yet", PolicyEngine.BlockReason(decision));
            string text = PolicyEngine.Explain(decision, Approve());
            Assert.StartsWith("Waiting for approval", text);
            Assert.Contains("Trust it", text);
        }

        [Theory]
        [InlineData(InputRule.Always, true, false)]
        [InlineData(InputRule.Approve, false, true)]
        [InlineData(InputRule.Trusted, false, false)]
        public void The_one_choice_maps_onto_the_two_switches(InputRule rule, bool allow, bool approve)
        {
            var policy = new Policy { Input = rule };
            Assert.Equal(allow, policy.AllowInput);
            Assert.Equal(approve, policy.ApproveInput);
            Assert.Equal(rule, policy.Input);
            Assert.Equal(rule, policy.Clone().Input);
            Assert.Equal(rule, Policy.FromJson(policy.ToJson()).Input);
        }

        [Fact]
        public void A_new_policy_keeps_keyboards_and_mice_working()
        {
            Assert.Equal(InputRule.Always, new Policy().Input);
            Assert.Equal(InputRule.Always, Policy.FromJson("{\"armed\":true}").Input);
            Assert.Equal(InputRule.Trusted, Policy.FromJson("{\"armed\":true,\"allowInput\":false}").Input);
        }

        [Fact]
        public void An_older_version_reads_the_rule_as_keyboards_need_trust()
        {
            string json = Approve(PnpMode.Block).ToJson();
            Assert.Contains("\"approveInput\": true", json);
            // 1.6 and earlier know only this field, and closed is the closest they have.
            Assert.Contains("\"allowInput\": false", json);
        }

        [Fact]
        public void The_rule_counts_as_a_setting_that_changed()
        {
            var one = new Policy { Armed = true };
            var two = new Policy { Armed = true, Input = InputRule.Approve };
            Assert.False(one.SettingsEqual(two));
            Assert.True(two.SettingsEqual(two.Clone()));
        }

        [Theory]
        [InlineData("\"approveInput\":\"yes\"")]
        [InlineData("\"approveInput\":1")]
        public void A_value_that_is_not_yes_or_no_is_refused(string field)
        {
            Assert.Throws<FormatException>(() => Policy.FromJson("{\"armed\":true," + field + "}"));
        }

        [Fact]
        public void With_strict_mode_every_new_device_is_held_because_any_of_them_could_be_a_keyboard()
        {
            Assert.Null(InstallRules.DenyFor(true, false, false));
            Assert.Equal(513, InstallRules.DenyFor(true, false, false, true).Count);
            Assert.Equal(513, InstallRules.DenyFor(true, true, false, true).Count);
            Assert.Null(InstallRules.DenyFor(false, false, false, true));
        }

        // ---------- the entry the service makes when it lets one in ----------

        [Fact]
        public void An_entry_for_a_device_that_was_only_let_in_is_marked_and_survives_saving()
        {
            var policy = new Policy { Input = InputRule.Approve };
            TrustEntry entry = TrustEntry.ForDevice(Devices.Mouse());
            entry.LetIn = true;
            policy.AddTrust(entry);
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));

            string json = policy.ToJson();
            Assert.Equal(1, json.Split(new[] { "\"letIn\"" }, StringSplitOptions.None).Length - 1);
            Policy read = Policy.FromJson(json);
            Assert.True(read.Trusted[0].LetIn);
            Assert.False(read.Trusted[1].LetIn);
            Assert.True(read.Clone().Trusted[0].LetIn);
        }

        [Fact]
        public void Trusting_such_a_device_approves_it()
        {
            var policy = new Policy { Armed = true, Input = InputRule.Approve };
            TrustEntry letIn = TrustEntry.ForDevice(Devices.Mouse());
            letIn.LetIn = true;
            policy.AddTrust(letIn);
            Decision before = PolicyEngine.Evaluate(policy, Devices.Mouse(), TimeSpan.MaxValue);
            Assert.Equal(Reason.Trusted, before.Reason);
            Assert.StartsWith("Let in without approval", PolicyEngine.Explain(before, policy));

            // The same entry made by a person takes its place.
            Assert.True(policy.AddTrust(TrustEntry.ForDevice(Devices.Mouse())));
            Assert.False(Assert.Single(policy.Trusted).LetIn);
            Assert.StartsWith("Trusted.", PolicyEngine.Explain(PolicyEngine.Evaluate(policy, Devices.Mouse(), TimeSpan.MaxValue), policy));
            // A second time it is simply there already, and the service cannot take an approval back.
            Assert.False(policy.AddTrust(TrustEntry.ForDevice(Devices.Mouse())));
            TrustEntry again = TrustEntry.ForDevice(Devices.Mouse());
            again.LetIn = true;
            Assert.False(policy.AddTrust(again));
            Assert.False(policy.Trusted.Single().LetIn);
        }

        [Fact]
        public void The_event_for_letting_one_in_has_a_number_of_its_own()
        {
            Assert.Equal(2102, EventIds.For(EventTypes.InputLetIn));
            Assert.Equal(EventLevel.Warning, EventIds.LevelFor(EventTypes.InputLetIn));
        }

#if NETFRAMEWORK
        // ---------- which keyboards and mice Windows has besides the USB ones ----------

        [Fact]
        public void A_keyboard_on_a_usb_port_is_left_to_the_usb_rules()
        {
            Assert.False(WindowsInput.CountsBesidesUsb(new[]
            {
                @"HID\VID_1A2C&PID_2D23&MI_00\7&3B19E5D3&0&0000", @"USB\VID_1A2C&PID_2D23&MI_00\6&C3A2486&0&0000",
                @"USB\VID_1A2C&PID_2D23\5&CC3F949&0&6", @"USB\ROOT_HUB30\4&1F5A3A6C&0&0", @"PCI\VEN_8086&DEV_7AE0\3&11583659&0&A0",
                @"ACPI\PNP0A08\0", @"ACPI_HAL\PNP0C08\0", @"ROOT\ACPI_HAL\0000", @"HTREE\ROOT\0"
            }));
        }

        [Fact]
        public void The_keyboard_and_the_touchpad_of_a_laptop_count()
        {
            Assert.True(WindowsInput.CountsBesidesUsb(new[]
            {
                @"ACPI\VEN_PNP&DEV_0303\4&1D401FB5&0", @"PCI\VEN_8086&DEV_A30D\3&11583659&0&F8", @"ACPI\PNP0A08\0",
                @"ACPI_HAL\PNP0C08\0", @"ROOT\ACPI_HAL\0000", @"HTREE\ROOT\0"
            }));
            Assert.True(WindowsInput.CountsBesidesUsb(new[]
            {
                @"HID\ELAN0501&COL01\5&2B8A4C01&0&0000", @"ACPI\ELAN0501\4&2F4A9A07&0", @"ACPI\PNP0A08\0",
                @"ACPI_HAL\PNP0C08\0", @"ROOT\ACPI_HAL\0000", @"HTREE\ROOT\0"
            }));
        }

        [Fact]
        public void A_keyboard_over_bluetooth_counts_also_when_the_radio_is_a_usb_device()
        {
            Assert.True(WindowsInput.CountsBesidesUsb(new[]
            {
                @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B342\8&2B1C1E5D&0&0000",
                @"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B342\7&1A2B3C4D&0&AABBCCDDEEFF_C00000000",
                @"BTH\MS_BTHBRB\6&2E8A1B0&0&1", @"USB\VID_2357&PID_0604\E848B8C82000", @"USB\ROOT_HUB30\4&1F5A3A6C&0&0",
                @"HTREE\ROOT\0"
            }));
            Assert.True(WindowsInput.CountsBesidesUsb(new[]
            {
                @"HID\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B023\9&1&0&0000",
                @"BTHLEDEVICE\{00001812-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D\8&1&0&0020",
                @"BTHLE\DEV_AABBCCDDEEFF\7&1&0&AABBCCDDEEFF", @"BTH\MS_BTHLE\6&1&0&3", @"ACPI\BCM2E7E\0", @"HTREE\ROOT\0"
            }));
        }

        [Fact]
        public void A_keyboard_that_exists_in_software_only_does_not_count()
        {
            // Remote Desktop adds these to every PC. Nobody can type on them at the computer.
            Assert.False(WindowsInput.CountsBesidesUsb(new[] { @"ROOT\RDP_KBD\0000", @"HTREE\ROOT\0" }));
            Assert.False(WindowsInput.CountsBesidesUsb(new[] { @"ROOT\RDP_MOU\0000", @"HTREE\ROOT\0" }));
            Assert.False(WindowsInput.CountsBesidesUsb(new[]
            {
                @"HID\VIRTUALKEYBOARD&COL01\2&1A2B&0&0000", @"ROOT\SYSTEM\0001", @"HTREE\ROOT\0"
            }));
            Assert.False(WindowsInput.CountsBesidesUsb(new string[0]));
            Assert.False(WindowsInput.CountsBesidesUsb(null));
        }

        [Fact]
        public void On_this_computer_the_question_can_be_asked()
        {
            // Whatever the answer is here, asking must not fail, and nothing it names is a USB device.
            foreach (string id in WindowsInput.FindBesidesUsb())
            {
                Assert.False(id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase));
            }
        }
#endif
    }
}
