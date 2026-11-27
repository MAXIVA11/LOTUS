using System;
using System.Collections.Generic;
using System.Linq;
using Lotus.Core;
#if NETFRAMEWORK
using Microsoft.Win32;
#endif
using Xunit;

namespace Lotus.Tests
{
    public class InstallRulesTests
    {
        [Fact]
        public void Every_usb_device_is_covered_with_the_spelling_Windows_uses()
        {
            List<string> ids = InstallRules.EveryUsbDevice();
            Assert.Equal(513, ids.Count);
            Assert.Contains(@"USB\COMPOSITE", ids);
            Assert.Contains(@"USB\Class_00", ids);
            Assert.Contains(@"USB\Class_08", ids);
            Assert.Contains(@"USB\Class_FF", ids);
            Assert.Contains(@"USB\DevClass_00", ids);
            Assert.Contains(@"USB\DevClass_EF", ids);
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void The_settings_decide_what_is_refused()
        {
            Assert.Null(InstallRules.DenyFor(false, true, true));
            Assert.Null(InstallRules.DenyFor(true, false, false));
            Assert.Equal(513, InstallRules.DenyFor(true, false, true).Count);
            Assert.Equal(513, InstallRules.DenyFor(true, true, true).Count);
            Assert.Equal(new[] { @"USB\Class_08", @"USB\Class_06", @"USB\MS_COMP_MTP" },
                InstallRules.DenyFor(true, true, false));
        }

        [Fact]
        public void A_plan_drops_duplicates_and_blanks()
        {
            var plan = new InstallPlan(new[] { @"USB\Class_08", @"usb\class_08", " ", null },
                new[] { @"usb\vid_0781&pid_5567\abc", @"USB\VID_0781&PID_5567\ABC", "" });
            Assert.Equal(new[] { @"USB\Class_08" }, plan.Deny);
            Assert.Equal(new[] { @"USB\VID_0781&PID_5567\ABC" }, plan.AllowInstances);
        }
    }

#if NETFRAMEWORK
    /// <summary>The real registry code, pointed at a key of its own under the current user.</summary>
    public class RegistryInstallGateTests : IDisposable
    {
        private readonly string root = @"Software\LotusTests\" + Guid.NewGuid().ToString("N");
        private readonly string path;
        private readonly RegistryInstallGate gate;

        public RegistryInstallGateTests()
        {
            path = root + @"\DeviceInstall\Restrictions";
            gate = new RegistryInstallGate(RegistryHive.CurrentUser, path, false);
        }

        public void Dispose()
        {
            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                user.DeleteSubKeyTree(root, false);
            }
        }

        private RegistryKey Open(string sub = null)
        {
            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                return user.OpenSubKey(sub == null ? path : path + "\\" + sub, true);
            }
        }

        private static InstallPlan Plan(params string[] allow)
        {
            return new InstallPlan(InstallRules.UsbStorage(), allow);
        }

        [Fact]
        public void Nothing_is_there_before_LOTUS_writes_it()
        {
            Assert.False(gate.Owned);
            Assert.False(gate.Foreign);
            Assert.False(gate.Matches(Plan()));
            Assert.Null(Open());
        }

        [Fact]
        public void The_plan_is_written_the_way_Windows_reads_it()
        {
            gate.Apply(Plan(@"USB\VID_0781&PID_5567\ABC"));

            Assert.True(gate.Owned);
            Assert.False(gate.Foreign);
            using (RegistryKey key = Open())
            {
                Assert.Equal(1, key.GetValue("AllowDenyLayered"));
                Assert.Equal(1, key.GetValue("DenyDeviceIDs"));
                Assert.Equal(0, key.GetValue("DenyDeviceIDsRetroactive"));
                Assert.Equal(1, key.GetValue("AllowInstanceIDs"));
                Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("DenyDeviceIDs"));
            }
            using (RegistryKey deny = Open("DenyDeviceIDs"))
            {
                Assert.Equal(new[] { "1", "2", "3" }, deny.GetValueNames().OrderBy(n => n).ToArray());
                Assert.Equal(RegistryValueKind.String, deny.GetValueKind("1"));
                var listed = deny.GetValueNames().Select(n => (string)deny.GetValue(n)).OrderBy(v => v).ToArray();
                Assert.Equal(InstallRules.UsbStorage().OrderBy(v => v).ToArray(), listed);
            }
            using (RegistryKey allow = Open("AllowInstanceIDs"))
            {
                Assert.Equal(@"USB\VID_0781&PID_5567\ABC", allow.GetValue("1"));
            }
            Assert.True(gate.Matches(Plan(@"usb\vid_0781&pid_5567\abc")));
            Assert.False(gate.Matches(Plan()));
            Assert.False(gate.Matches(new InstallPlan(InstallRules.EveryUsbDevice(), new[] { @"USB\VID_0781&PID_5567\ABC" })));
        }

        [Fact]
        public void The_whole_list_of_usb_classes_fits()
        {
            var plan = new InstallPlan(InstallRules.EveryUsbDevice(), new string[0]);
            gate.Apply(plan);
            Assert.True(gate.Matches(plan));
            using (RegistryKey deny = Open("DenyDeviceIDs")) Assert.Equal(513, deny.ValueCount);
        }

        [Fact]
        public void One_more_device_can_be_let_through()
        {
            gate.Apply(Plan(@"USB\VID_0781&PID_5567\ABC"));
            gate.AllowInstance(@"usb\vid_046d&pid_c542\5&cc3f949&0&5");
            gate.AllowInstance(@"USB\VID_046D&PID_C542\5&CC3F949&0&5"); // twice is once

            using (RegistryKey allow = Open("AllowInstanceIDs")) Assert.Equal(2, allow.ValueCount);
            Assert.True(gate.Matches(Plan(@"USB\VID_0781&PID_5567\ABC", @"USB\VID_046D&PID_C542\5&CC3F949&0&5")));
        }

        [Fact]
        public void Letting_a_device_through_does_nothing_while_the_restrictions_are_off()
        {
            gate.AllowInstance(@"USB\VID_046D&PID_C542\5&CC3F949&0&5");
            Assert.Null(Open());
        }

        [Fact]
        public void A_new_plan_replaces_the_old_lists()
        {
            gate.Apply(Plan("A", "B", "C"));
            gate.Apply(new InstallPlan(InstallRules.EveryUsbDevice(), new[] { "B" }));

            using (RegistryKey allow = Open("AllowInstanceIDs")) Assert.Equal(1, allow.ValueCount);
            using (RegistryKey deny = Open("DenyDeviceIDs")) Assert.Equal(513, deny.ValueCount);
        }

        [Fact]
        public void A_change_made_by_someone_else_is_noticed()
        {
            gate.Apply(Plan("A"));
            using (RegistryKey allow = Open("AllowInstanceIDs")) allow.SetValue("99", "INTRUDER");
            Assert.False(gate.Matches(Plan("A")));

            gate.Apply(Plan("A"));
            Assert.True(gate.Matches(Plan("A")));

            using (RegistryKey key = Open()) key.SetValue("DenyDeviceIDs", 0, RegistryValueKind.DWord);
            Assert.False(gate.Matches(Plan("A")));
        }

        [Fact]
        public void Removing_leaves_no_key_behind()
        {
            gate.Apply(Plan("A"));
            gate.Remove();

            Assert.False(gate.Owned);
            Assert.Null(Open());
            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            {
                Assert.Null(user.OpenSubKey(root + @"\DeviceInstall"));
            }
            gate.Remove(); // a second time is harmless
        }

        [Fact]
        public void Restrictions_set_by_someone_else_are_never_touched()
        {
            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (RegistryKey key = user.CreateSubKey(path))
            {
                key.SetValue("DenyUnspecified", 1, RegistryValueKind.DWord);
                key.SetValue("AllowAdminInstall", 1, RegistryValueKind.DWord);
            }

            Assert.True(gate.Foreign);
            Assert.False(gate.Owned);
            Assert.Throws<InvalidOperationException>(() => gate.Apply(Plan("A")));
            gate.Remove();
            gate.AllowInstance("A");

            using (RegistryKey key = Open())
            {
                Assert.Equal(1, key.GetValue("DenyUnspecified"));
                Assert.Equal(1, key.GetValue("AllowAdminInstall"));
                Assert.Null(key.GetValue("ManagedByLOTUS"));
                Assert.Equal(0, key.SubKeyCount);
            }
        }

        [Fact]
        public void A_setting_of_someone_else_that_restricts_nothing_stays_when_LOTUS_leaves()
        {
            using (RegistryKey user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64))
            using (RegistryKey key = user.CreateSubKey(path))
            {
                key.SetValue("ForceReboot", 0, RegistryValueKind.DWord);
            }
            Assert.False(gate.Foreign);

            gate.Apply(Plan("A"));
            gate.Remove();

            using (RegistryKey key = Open())
            {
                Assert.NotNull(key);
                Assert.Equal(new[] { "ForceReboot" }, key.GetValueNames());
            }
        }
    }
#endif

    /// <summary>The enforcer against a pretend Windows that refuses new devices the way the real one does.</summary>
    public class StrictModeTests
    {
        private readonly SimulatedBackend bus = new SimulatedBackend();
        private readonly SimulatedInstallGate gate = new SimulatedInstallGate();
        private readonly Ledger ledger = new Ledger();
        private readonly List<LotusEvent> events = new List<LotusEvent>();
        private DateTime now = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        private readonly Enforcer enforcer;

        public StrictModeTests()
        {
            bus.Gate = gate;
            enforcer = new Enforcer(bus, new SimulatedReadOnlyPolicy(), ledger, null, events.Add, () => now, gate);
        }

        private static Policy StorageBlocked()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block };
        }

        private static Policy TrustedOnly()
        {
            return new Policy { Armed = true, Pnp = PnpMode.Block, AllowInput = true };
        }

        private UsbDevice On(string id)
        {
            return bus.Scan().Single(d => d.InstanceId == id);
        }

        /// <summary>Lets the second pass by that LOTUS gives Windows for its own installation attempt.</summary>
        private void AfterWindowsHadItsGo(Policy policy)
        {
            now = now.AddSeconds(1.1);
            enforcer.RunOnce(policy);
        }

        private static UsbDevice Headset()
        {
            UsbDevice headset = Devices.Headset();
            headset.Described = true;
            return headset;
        }

        [Fact]
        public void Arming_with_trusted_only_has_Windows_refuse_every_new_usb_device()
        {
            enforcer.RunOnce(TrustedOnly());

            Assert.True(gate.Owned);
            Assert.Equal(513, gate.Plan.Deny.Count);
            Assert.Contains(events, e => e.Type == EventTypes.PolicyChanged && e.Message == SystemWords.GateClosed);
        }

        [Fact]
        public void With_only_storage_blocked_only_storage_is_refused()
        {
            enforcer.RunOnce(StorageBlocked());
            Assert.Equal(InstallRules.UsbStorage().OrderBy(v => v), gate.Plan.Deny.OrderBy(v => v));

            bus.Plug(Devices.Mouse());
            Assert.True(On(Devices.MouseId).Started);
        }

        [Fact]
        public void Nothing_is_refused_unless_the_policy_blocks_something_and_asks_for_it()
        {
            enforcer.RunOnce(new Policy { Storage = StorageMode.Block, Pnp = PnpMode.Block }); // disarmed
            Assert.False(gate.Owned);

            enforcer.RunOnce(new Policy { Armed = true, Storage = StorageMode.ReadOnly });
            Assert.False(gate.Owned);

            Policy relaxed = TrustedOnly();
            relaxed.Strict = false;
            enforcer.RunOnce(relaxed);
            Assert.False(gate.Owned);
            Assert.Equal(0, gate.ApplyCalls);
        }

        [Fact]
        public void The_lockdown_policy_refuses_new_devices_too()
        {
            Assert.True(GuardWorker.LockdownPolicy().Strict);
        }

        [Fact]
        public void Devices_that_run_already_keep_running_and_are_named_as_exceptions()
        {
            bus.Plug(Devices.Mouse());
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(TrustedOnly()); // storage is allowed, input is allowed

            Assert.True(On(Devices.MouseId).Started);
            Assert.True(On(Devices.StickId).Started);
            Assert.True(gate.Allows(Devices.MouseId));
            Assert.True(gate.Allows(Devices.StickId));
        }

        [Fact]
        public void A_new_device_the_policy_blocks_never_starts()
        {
            enforcer.RunOnce(StorageBlocked());

            bus.Plug(Devices.Stick());
            UsbDevice arrived = On(Devices.StickId);
            Assert.False(arrived.Started);
            Assert.True(arrived.InstallRefused);

            DeviceStatus status = enforcer.FastPass(StorageBlocked(), Devices.StickId);
            Assert.Equal(DeviceState.Blocked, status.State);
            Assert.True(On(Devices.StickId).IsOff);
            Assert.True(ledger.Contains(Devices.StickId));
            Assert.Contains(events, e => e.Type == EventTypes.Blocked && e.DeviceId == Devices.StickId);
            Assert.Equal(0, bus.ReinstallCalls);
        }

        [Fact]
        public void A_new_device_the_policy_allows_is_let_in()
        {
            enforcer.RunOnce(TrustedOnly());

            bus.Plug(Devices.Mouse()); // a mouse, and keyboards and mice stay allowed
            Assert.True(On(Devices.MouseId).InstallRefused);

            // The exception is named at once. Windows gets a second for its own
            // attempt before LOTUS has it start over.
            enforcer.FastPass(TrustedOnly(), Devices.MouseId);
            Assert.True(gate.Allows(Devices.MouseId));
            Assert.Equal(0, bus.ReinstallCalls);

            AfterWindowsHadItsGo(TrustedOnly());
            Assert.True(On(Devices.MouseId).Started);
            Assert.Equal(1, bus.ReinstallCalls);
            Assert.DoesNotContain(events, e => e.Type == EventTypes.Blocked);
        }

        [Fact]
        public void A_device_trusted_by_its_own_id_starts_without_waiting_for_LOTUS()
        {
            Policy policy = StorageBlocked();
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            enforcer.RunOnce(policy);
            Assert.True(gate.Allows(Devices.StickId)); // although it is not plugged in

            bus.Plug(Devices.Stick());
            Assert.True(On(Devices.StickId).Started);
            Assert.Equal(0, bus.ReinstallCalls);
        }

        [Fact]
        public void A_device_trusted_as_a_model_is_let_in_when_it_arrives()
        {
            Policy policy = StorageBlocked();
            policy.AddTrust(TrustEntry.ForModel(Devices.Stick()));
            enforcer.RunOnce(policy);

            bus.Plug(Devices.Stick());
            Assert.True(On(Devices.StickId).InstallRefused);
            enforcer.FastPass(policy, Devices.StickId);
            AfterWindowsHadItsGo(policy);
            Assert.True(On(Devices.StickId).Started);
        }

        [Fact]
        public void The_functions_of_an_allowed_device_are_let_in_too()
        {
            Policy policy = TrustedOnly();
            policy.AddTrust(TrustEntry.ForDevice(Headset()));
            enforcer.RunOnce(policy);

            bus.Plug(Headset());
            Assert.True(On(Devices.HeadsetId).Started); // trusted by its own ID
            List<UsbChild> functions = bus.Children(Devices.HeadsetId);
            Assert.Equal(2, functions.Count);
            Assert.All(functions, f => Assert.True(f.InstallRefused));

            enforcer.FastPass(policy, Devices.HeadsetId);
            Assert.All(bus.Children(Devices.HeadsetId), f => Assert.True(gate.Allows(f.InstanceId)));
            AfterWindowsHadItsGo(policy);
            Assert.All(bus.Children(Devices.HeadsetId), f => Assert.True(f.Started));
            Assert.Equal(2, bus.ReinstallCalls);
        }

        [Fact]
        public void A_function_that_arrives_on_its_own_is_let_in_when_its_device_is_allowed()
        {
            Policy policy = TrustedOnly();
            policy.AddTrust(TrustEntry.ForDevice(Headset()));
            enforcer.RunOnce(policy);
            bus.Plug(Headset());
            string first = bus.Children(Devices.HeadsetId)[0].InstanceId;

            string second = bus.Children(Devices.HeadsetId)[1].InstanceId;

            Assert.Null(enforcer.FastPass(policy, first));
            Assert.True(gate.Allows(first));
            Assert.False(gate.Allows(second));

            now = now.AddSeconds(1.1);
            Assert.Null(enforcer.FastPass(policy, first));
            Assert.True(bus.Children(Devices.HeadsetId)[0].Started);
            Assert.False(bus.Children(Devices.HeadsetId)[1].Started);
        }

        [Fact]
        public void A_function_of_a_device_the_policy_blocks_is_not_let_in()
        {
            enforcer.RunOnce(StorageBlocked());
            UsbDevice combo = Devices.StorageKeyboardCombo();
            combo.Described = true;
            bus.Plug(combo);
            // The device itself is not storage as a whole, so Windows starts it and
            // refuses its storage function.
            Assert.True(On(Devices.ComboId).Started);
            UsbChild storage = bus.Children(Devices.ComboId)[0];
            Assert.True(storage.InstallRefused);

            enforcer.FastPass(StorageBlocked(), storage.InstanceId);
            Assert.True(bus.Children(Devices.ComboId)[0].InstallRefused);
            Assert.Equal(0, bus.ReinstallCalls);

            // And the device as a whole is blocked, because it has storage.
            enforcer.FastPass(StorageBlocked(), Devices.ComboId);
            Assert.True(On(Devices.ComboId).IsOff);
        }

        [Fact]
        public void Trusting_a_device_Windows_refused_installs_it()
        {
            enforcer.RunOnce(StorageBlocked());
            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId);
            Assert.True(On(Devices.StickId).IsOff);

            Policy trusting = StorageBlocked();
            trusting.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            enforcer.RunOnce(trusting);

            Assert.True(On(Devices.StickId).Started);
            Assert.False(ledger.Contains(Devices.StickId));
            Assert.Contains(events, e => e.Type == EventTypes.Released && e.DeviceId == Devices.StickId);
        }

        [Fact]
        public void A_pass_with_a_device_still_being_let_in_reports_unfinished_business()
        {
            enforcer.RunOnce(TrustedOnly());
            bus.Plug(Devices.Mouse());

            Assert.Equal(1, enforcer.RunOnce(TrustedOnly()).Pending);
            now = now.AddSeconds(1.1);
            Assert.Equal(1, enforcer.RunOnce(TrustedOnly()).Pending); // Windows was just asked to start over
            now = now.AddSeconds(0.3);
            Assert.Equal(0, enforcer.RunOnce(TrustedOnly()).Pending); // and the device runs
        }

        [Fact]
        public void Disarming_opens_the_door_and_installs_what_was_refused()
        {
            enforcer.RunOnce(StorageBlocked());
            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId); // refused, then switched off
            bus.Plug(Devices.Phone()); // refused, and LOTUS has not looked at it yet
            Assert.True(On(Devices.PhoneId).InstallRefused);

            enforcer.RunOnce(new Policy());

            Assert.False(gate.Owned);
            Assert.True(On(Devices.StickId).Started);
            Assert.True(On(Devices.PhoneId).Started);
            Assert.Equal(0, ledger.Count);
            Assert.Contains(events, e => e.Type == EventTypes.PolicyChanged && e.Message == SystemWords.GateOpened);
        }

        [Fact]
        public void Switching_strict_off_while_armed_opens_the_door_but_keeps_blocking()
        {
            enforcer.RunOnce(StorageBlocked());
            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId);

            Policy relaxed = StorageBlocked();
            relaxed.Strict = false;
            enforcer.RunOnce(relaxed);

            Assert.False(gate.Owned);
            Assert.True(On(Devices.StickId).IsOff); // still blocked, the old way
            Assert.True(ledger.Contains(Devices.StickId));
        }

        [Fact]
        public void Removing_LOTUS_takes_the_restrictions_away_and_installs_what_was_refused()
        {
            enforcer.RunOnce(StorageBlocked());
            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId);
            bus.Plug(Devices.Phone());

            Assert.Equal(0, enforcer.ReleaseEverything());

            Assert.False(gate.Owned);
            Assert.Equal(1, gate.RemoveCalls);
            Assert.True(On(Devices.StickId).Started);
            Assert.True(On(Devices.PhoneId).Started);
        }

        [Fact]
        public void Restrictions_set_by_someone_else_are_left_alone_and_blocking_still_works()
        {
            gate.ForeignPolicy = true;
            enforcer.RunOnce(StorageBlocked());
            enforcer.RunOnce(StorageBlocked());

            Assert.False(gate.Owned);
            Assert.Equal(0, gate.ApplyCalls);
            Assert.Single(events, e => e.Type == EventTypes.Warning && e.Message == SystemWords.GateForeign);

            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId);
            Assert.True(On(Devices.StickId).IsOff);
        }

        [Fact]
        public void On_a_Windows_that_cannot_refuse_devices_LOTUS_says_so_once_and_blocks_the_old_way()
        {
            gate.Supported = false;
            enforcer.RunOnce(StorageBlocked());
            enforcer.RunOnce(StorageBlocked());

            Assert.False(gate.Owned);
            Assert.Single(events, e => e.Type == EventTypes.Warning && e.Message == SystemWords.GateUnsupported);

            bus.Plug(Devices.Stick());
            enforcer.FastPass(StorageBlocked(), Devices.StickId);
            Assert.True(On(Devices.StickId).IsOff);
        }

        [Fact]
        public void A_device_Windows_cannot_install_is_given_up_on_after_three_tries()
        {
            enforcer.RunOnce(TrustedOnly());
            bus.NoDriver.Add(Devices.MouseId);
            bus.Plug(Devices.Mouse());

            for (int pass = 0; pass < 8; pass++)
            {
                now = now.AddSeconds(2);
                enforcer.RunOnce(TrustedOnly());
            }

            Assert.Equal(3, bus.ReinstallCalls);
            Assert.Single(events, e => e.Type == EventTypes.Warning && e.Message == SystemWords.NotLetIn("Wireless Receiver"));

            // Unplugging and plugging in again starts over.
            bus.Unplug(Devices.MouseId);
            enforcer.RunOnce(TrustedOnly());
            bus.NoDriver.Clear();
            bus.Plug(Devices.Mouse());
            enforcer.FastPass(TrustedOnly(), Devices.MouseId);
            AfterWindowsHadItsGo(TrustedOnly());
            Assert.True(On(Devices.MouseId).Started);
        }

        [Fact]
        public void A_device_that_was_just_let_in_is_not_let_in_again_while_Windows_installs_it()
        {
            enforcer.RunOnce(TrustedOnly());
            bus.NoDriver.Add(Devices.MouseId); // stands in for "still installing": it keeps looking refused
            bus.Plug(Devices.Mouse());

            enforcer.FastPass(TrustedOnly(), Devices.MouseId);
            now = now.AddMilliseconds(40);
            enforcer.FastPass(TrustedOnly(), Devices.MouseId); // Windows reports the same device again
            now = now.AddMilliseconds(40);
            PassResult early = enforcer.RunOnce(TrustedOnly());
            Assert.Equal(0, bus.ReinstallCalls); // Windows is still at it
            Assert.Equal(1, early.Pending);

            AfterWindowsHadItsGo(TrustedOnly());
            Assert.Equal(1, bus.ReinstallCalls);
            now = now.AddSeconds(1);
            enforcer.RunOnce(TrustedOnly());
            Assert.Equal(1, bus.ReinstallCalls); // and now it gets three seconds for the new attempt

            now = now.AddSeconds(4);
            enforcer.RunOnce(TrustedOnly());
            Assert.Equal(2, bus.ReinstallCalls);
        }

        [Fact]
        public void A_device_that_had_no_driver_before_LOTUS_was_armed_is_left_alone()
        {
            bus.NoDriver.Add(Devices.MouseId);
            bus.Plug(Devices.Mouse());
            Assert.True(On(Devices.MouseId).InstallRefused);

            for (int pass = 0; pass < 4; pass++)
            {
                now = now.AddSeconds(2);
                enforcer.RunOnce(TrustedOnly());
            }

            Assert.True(gate.Owned);
            Assert.Equal(0, bus.ReinstallCalls);
            Assert.DoesNotContain(events, e => e.Type == EventTypes.Warning);
        }

        [Fact]
        public void The_exceptions_follow_what_is_plugged_in()
        {
            enforcer.RunOnce(TrustedOnly());
            bus.Plug(Devices.Mouse());
            enforcer.FastPass(TrustedOnly(), Devices.MouseId);
            AfterWindowsHadItsGo(TrustedOnly());
            Assert.True(gate.Allows(Devices.MouseId));

            bus.Unplug(Devices.MouseId);
            enforcer.RunOnce(TrustedOnly());
            Assert.True(gate.Allows(Devices.MouseId)); // still within the moment Windows may need

            now = now.AddMinutes(2);
            enforcer.RunOnce(TrustedOnly());
            Assert.False(gate.Allows(Devices.MouseId));
        }

        [Fact]
        public void The_plan_is_only_written_when_it_changes()
        {
            bus.Plug(Devices.Mouse());
            enforcer.RunOnce(TrustedOnly());
            enforcer.RunOnce(TrustedOnly());
            enforcer.RunOnce(TrustedOnly());
            Assert.Equal(1, gate.ApplyCalls);
        }

        [Fact]
        public void A_blocked_device_is_never_on_the_list_of_exceptions()
        {
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(StorageBlocked());
            enforcer.RunOnce(StorageBlocked());

            Assert.True(On(Devices.StickId).IsOff);
            Assert.False(gate.Allows(Devices.StickId));
        }

        [Fact]
        public void The_setting_survives_a_round_trip_and_defaults_to_on()
        {
            Policy off = TrustedOnly();
            off.Strict = false;
            Assert.False(Policy.FromJson(off.ToJson()).Strict);
            Assert.True(Policy.FromJson(TrustedOnly().ToJson()).Strict);
            // A policy written before the setting existed.
            Assert.True(Policy.FromJson("{\"version\":1,\"armed\":true,\"storage\":\"block\",\"pnp\":\"allow\"}").Strict);
            Assert.False(off.SettingsEqual(TrustedOnly()));
            Assert.False(off.Clone().Strict);
        }
    }
}
