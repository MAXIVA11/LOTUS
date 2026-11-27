using System;
using System.Collections.Generic;
using System.Linq;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    public class EnforcerTests
    {
        private readonly SimulatedBackend bus = new SimulatedBackend();
        private readonly SimulatedReadOnlyPolicy readOnly = new SimulatedReadOnlyPolicy();
        private readonly Ledger ledger = new Ledger();
        private readonly List<LotusEvent> events = new List<LotusEvent>();
        private DateTime now = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        private int ledgerSaves;
        private readonly Enforcer enforcer;

        public EnforcerTests()
        {
            enforcer = new Enforcer(bus, readOnly, ledger, _ => ledgerSaves++, events.Add, () => now);
        }

        private static Policy Armed(StorageMode storage, PnpMode pnp, bool allowInput = true)
        {
            return new Policy { Armed = true, Storage = storage, Pnp = pnp, AllowInput = allowInput };
        }

        private UsbDevice Live(string id)
        {
            return bus.Scan().Single(d => d.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        private DeviceStatus StatusOf(PassResult result, string id)
        {
            return result.Devices.Single(s => s.Device.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        private int Count(string type)
        {
            return events.Count(e => e.Type == type);
        }

        [Fact]
        public void Disarmed_touches_nothing()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Keyboard());

            PassResult result = enforcer.RunOnce(new Policy());

            Assert.Equal(0, bus.BlockCalls);
            Assert.Equal(0, ledger.Count);
            Assert.All(result.Devices, s => Assert.Equal(DeviceState.Allowed, s.State));
            Assert.Empty(events);
        }

        [Fact]
        public void Storage_block_switches_off_drives_and_leaves_the_rest()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Keyboard());
            bus.Plug(Devices.Phone());

            PassResult result = enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Allow));

            Assert.True(Live(Devices.StickId).IsOff);
            Assert.True(Live(Devices.PhoneId).IsOff);
            Assert.False(Live(Devices.KeyboardId).IsOff);
            Assert.Equal(2, result.Blocked);
            Assert.Equal(DeviceState.Blocked, StatusOf(result, Devices.StickId).State);
            Assert.True(ledger.Contains(Devices.StickId));
            Assert.Equal(2, Count(EventTypes.Blocked));
        }

        [Fact]
        public void A_blocked_device_is_not_blocked_again_on_later_passes()
        {
            bus.Plug(Devices.Stick());
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            enforcer.RunOnce(policy);
            enforcer.RunOnce(policy);
            PassResult third = enforcer.RunOnce(policy);

            Assert.Equal(1, bus.BlockCalls);
            Assert.Equal(1, Count(EventTypes.Blocked));
            Assert.Equal(DeviceState.Blocked, StatusOf(third, Devices.StickId).State);
            Assert.Equal("SanDisk Cruzer Blade USB Device", StatusOf(third, Devices.StickId).Device.Name);
        }

        [Fact]
        public void Disarming_switches_everything_back_on()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Headset());
            enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Block));
            Assert.True(Live(Devices.StickId).IsOff);
            Assert.True(Live(Devices.HeadsetId).IsOff);

            PassResult result = enforcer.RunOnce(new Policy());

            Assert.False(Live(Devices.StickId).IsOff);
            Assert.False(Live(Devices.HeadsetId).IsOff);
            Assert.Equal(0, ledger.Count);
            Assert.Equal(2, result.Released);
            Assert.False(bus.HasDisabledMark(Devices.StickId));
            Assert.Equal(DeviceState.Allowed, StatusOf(result, Devices.StickId).State);
        }

        [Fact]
        public void Trusting_a_blocked_device_switches_it_back_on()
        {
            bus.Plug(Devices.Headset());
            Policy policy = Armed(StorageMode.Allow, PnpMode.Block);
            enforcer.RunOnce(policy);
            Assert.True(Live(Devices.HeadsetId).IsOff);

            // The app builds the trust entry from what it shows, and what it shows
            // comes from the ledger because the device itself is switched off.
            DeviceStatus shown = DeviceView.Resolve(policy, Live(Devices.HeadsetId), ledger, TimeSpan.MaxValue);
            Assert.Equal(DeviceKinds.Audio | DeviceKinds.Input, shown.Device.Kinds);
            policy.AddTrust(TrustEntry.ForDevice(shown.Device));

            PassResult result = enforcer.RunOnce(policy);

            Assert.False(Live(Devices.HeadsetId).IsOff);
            Assert.Equal(DeviceState.Trusted, StatusOf(result, Devices.HeadsetId).State);
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void A_keyboard_is_blocked_even_though_windows_refuses_to_disable_it()
        {
            bus.NotDisableable.Add(Devices.KeyboardId);
            bus.Plug(Devices.Keyboard());
            Policy policy = Armed(StorageMode.Allow, PnpMode.Block, allowInput: false);

            enforcer.RunOnce(policy);

            Assert.True(Live(Devices.KeyboardId).IsOff);
            Assert.Equal(UsbDevice.ProblemRemoved, Live(Devices.KeyboardId).Problem);
            Assert.True(ledger.Contains(Devices.KeyboardId));

            enforcer.RunOnce(new Policy());
            Assert.False(Live(Devices.KeyboardId).IsOff);
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void The_keyboard_safety_net_keeps_keyboards_working()
        {
            bus.Plug(Devices.Keyboard());
            bus.Plug(Devices.Mouse());
            enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Block, allowInput: true));
            Assert.False(Live(Devices.KeyboardId).IsOff);
            Assert.False(Live(Devices.MouseId).IsOff);
            Assert.Equal(0, bus.BlockCalls);
        }

        [Fact]
        public void A_device_switched_off_by_hand_is_left_alone()
        {
            bus.Plug(Devices.Headset());
            bus.DisableByHand(Devices.HeadsetId);

            PassResult armed = enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Block));
            Assert.Equal(DeviceState.OffElsewhere, StatusOf(armed, Devices.HeadsetId).State);
            Assert.Equal(0, ledger.Count);

            enforcer.RunOnce(new Policy());
            Assert.True(Live(Devices.HeadsetId).IsOff);
            Assert.Equal(0, bus.UnblockCalls);
        }

        [Fact]
        public void A_blocked_device_stays_blocked_when_replugged_and_says_so()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(policy);
            bus.Unplug(Devices.StickId);
            enforcer.RunOnce(policy);
            Assert.True(ledger.Contains(Devices.StickId));

            bus.Plug(Devices.Stick());
            Assert.True(Live(Devices.StickId).IsOff);
            events.Clear();
            enforcer.RunOnce(policy);

            Assert.Equal(1, bus.BlockCalls);
            Assert.Equal(1, Count(EventTypes.Connected));
            Assert.Equal(1, Count(EventTypes.Blocked));
        }

        [Fact]
        public void A_blocked_device_that_was_unplugged_is_cleaned_up_when_the_policy_relaxes()
        {
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Allow));
            bus.Unplug(Devices.StickId);
            Assert.True(bus.HasDisabledMark(Devices.StickId));

            enforcer.RunOnce(new Policy());

            Assert.False(bus.HasDisabledMark(Devices.StickId));
            Assert.Equal(0, ledger.Count);

            bus.Plug(Devices.Stick());
            Assert.False(Live(Devices.StickId).IsOff);
        }

        [Fact]
        public void An_unplugged_blocked_device_stays_in_the_ledger_while_the_policy_still_blocks_it()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(policy);
            bus.Unplug(Devices.StickId);
            enforcer.RunOnce(policy);
            enforcer.RunOnce(policy);
            Assert.True(ledger.Contains(Devices.StickId));
            Assert.Equal(0, bus.UnblockCalls);
        }

        [Fact]
        public void A_fresh_composite_device_is_judged_once_it_identifies_itself()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            enforcer.RunOnce(policy);

            bus.Plug(Devices.FreshComposite());
            PassResult first = enforcer.RunOnce(policy);
            Assert.Equal(DeviceState.Identifying, StatusOf(first, Devices.ComboId).State);
            Assert.Equal(0, bus.BlockCalls);

            // Its interfaces show up: storage plus a keyboard.
            bus.Plug(Devices.StorageKeyboardCombo());
            now = now.AddMilliseconds(300);
            enforcer.RunOnce(policy);

            Assert.True(Live(Devices.ComboId).IsOff);
            Assert.Equal(Reason.StorageBlocked, ledger.Get(Devices.ComboId).Reason);
            Assert.Equal(DeviceKinds.Storage | DeviceKinds.Input, ledger.Get(Devices.ComboId).Kinds);
        }

        [Fact]
        public void A_device_that_never_identifies_itself_is_blocked_under_the_pnp_rule()
        {
            Policy policy = Armed(StorageMode.Allow, PnpMode.Block);
            enforcer.RunOnce(policy);
            bus.Plug(Devices.FreshComposite());

            enforcer.RunOnce(policy);
            Assert.False(Live(Devices.ComboId).IsOff);

            now = now + PolicyEngine.IdentifyGrace;
            enforcer.RunOnce(policy);
            Assert.True(Live(Devices.ComboId).IsOff);
        }

        [Fact]
        public void Devices_present_at_startup_are_not_kept_waiting()
        {
            bus.Plug(Devices.FreshComposite());
            enforcer.RunOnce(Armed(StorageMode.Allow, PnpMode.Block));
            Assert.True(Live(Devices.ComboId).IsOff);
        }

        [Fact]
        public void A_refused_block_is_retried_and_reported_once()
        {
            bus.Plug(Devices.Stick());
            bus.RefuseBlock.Add(Devices.StickId);
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);

            PassResult first = enforcer.RunOnce(policy);
            enforcer.RunOnce(policy);
            enforcer.RunOnce(policy);

            Assert.Equal(3, bus.BlockCalls);
            Assert.Equal(1, first.Failures);
            Assert.Equal(DeviceState.Blocking, StatusOf(first, Devices.StickId).State);
            Assert.Equal(1, Count(EventTypes.Warning));
            // The ledger entry is written before the attempt, so a crash cannot orphan the device.
            Assert.True(ledger.Contains(Devices.StickId));

            bus.RefuseBlock.Clear();
            enforcer.RunOnce(policy);
            Assert.True(Live(Devices.StickId).IsOff);
            Assert.Equal(1, Count(EventTypes.Blocked));
        }

        [Fact]
        public void A_refused_release_is_retried()
        {
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Allow));
            bus.RefuseUnblock.Add(Devices.StickId);

            enforcer.RunOnce(new Policy());
            Assert.True(ledger.Contains(Devices.StickId));

            bus.RefuseUnblock.Clear();
            enforcer.RunOnce(new Policy());
            Assert.False(Live(Devices.StickId).IsOff);
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void Read_only_turns_the_windows_setting_on_and_off()
        {
            bus.Plug(Devices.Stick());

            PassResult on = enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Allow));
            Assert.True(readOnly.Applied);
            Assert.True(on.ReadOnlyActive);
            Assert.True(ledger.ReadOnlyOwned);
            Assert.Equal(1, Count(EventTypes.ReadOnlyOn));
            // The connected drive was restarted so the setting reaches it, and is on again.
            Assert.Equal(1, bus.BlockCalls);
            Assert.False(Live(Devices.StickId).IsOff);
            Assert.Equal(0, ledger.Count);
            Assert.Equal(0, Count(EventTypes.Blocked));

            enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Allow));
            Assert.Equal(1, readOnly.ApplyCalls);
            Assert.Equal(1, bus.BlockCalls);

            PassResult off = enforcer.RunOnce(Armed(StorageMode.Allow, PnpMode.Allow));
            Assert.False(readOnly.Applied);
            Assert.False(off.ReadOnlyActive);
            Assert.False(ledger.ReadOnlyOwned);
            Assert.Equal(1, Count(EventTypes.ReadOnlyOff));
        }

        [Fact]
        public void Disarming_lifts_read_only()
        {
            enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Allow));
            Assert.True(readOnly.Applied);
            enforcer.RunOnce(new Policy { Armed = false, Storage = StorageMode.ReadOnly });
            Assert.False(readOnly.Applied);
        }

        [Fact]
        public void A_read_only_setting_someone_else_made_is_not_removed()
        {
            readOnly.Applied = true;
            enforcer.RunOnce(new Policy());
            enforcer.RunOnce(Armed(StorageMode.Allow, PnpMode.Allow));
            Assert.True(readOnly.Applied);
            Assert.Equal(0, readOnly.RemoveCalls);
        }

        [Fact]
        public void Read_only_is_put_back_if_someone_removes_it()
        {
            Policy policy = Armed(StorageMode.ReadOnly, PnpMode.Allow);
            enforcer.RunOnce(policy);
            readOnly.Applied = false;
            enforcer.RunOnce(policy);
            Assert.True(readOnly.Applied);
            Assert.Equal(2, readOnly.ApplyCalls);
        }

        [Fact]
        public void A_registry_failure_does_not_stop_device_blocking()
        {
            readOnly.Throw = true;
            bus.Plug(Devices.Headset());

            PassResult result = enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Block));
            enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Block));

            Assert.True(Live(Devices.HeadsetId).IsOff);
            Assert.False(result.ReadOnlyActive);
            Assert.Equal(1, Count(EventTypes.Error));
        }

        [Fact]
        public void The_system_disk_is_never_restarted_or_blocked()
        {
            UsbDevice disk = Devices.Stick();
            disk.IsSystemDisk = true;
            bus.Plug(disk);

            enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Block));
            enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Block));

            Assert.Equal(0, bus.BlockCalls);
            Assert.False(Live(Devices.StickId).IsOff);
        }

        [Fact]
        public void Connect_and_disconnect_are_logged_with_the_device_name()
        {
            enforcer.RunOnce(new Policy());
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(new Policy());
            bus.Unplug(Devices.StickId);
            enforcer.RunOnce(new Policy());

            Assert.Equal("SanDisk Cruzer Blade USB Device was connected.",
                events.Single(e => e.Type == EventTypes.Connected).Message);
            Assert.Equal("SanDisk Cruzer Blade USB Device was disconnected.",
                events.Single(e => e.Type == EventTypes.Removed).Message);
        }

        [Fact]
        public void Devices_already_connected_at_startup_are_not_announced()
        {
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(new Policy());
            Assert.Equal(0, Count(EventTypes.Connected));
        }

        [Fact]
        public void Release_everything_undoes_all_of_it()
        {
            bus.Plug(Devices.Stick());
            bus.Plug(Devices.Headset());
            bus.Plug(Devices.Phone());
            enforcer.RunOnce(Armed(StorageMode.Block, PnpMode.Block));
            bus.Unplug(Devices.PhoneId);
            enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Block));
            Assert.True(readOnly.Applied);

            int problems = enforcer.ReleaseEverything();

            Assert.Equal(0, problems);
            Assert.Equal(0, ledger.Count);
            Assert.False(readOnly.Applied);
            Assert.False(Live(Devices.HeadsetId).IsOff);
            Assert.False(bus.HasDisabledMark(Devices.PhoneId));
        }

        [Fact]
        public void Release_everything_restarts_a_connected_drive_so_that_it_is_writable_again()
        {
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(Armed(StorageMode.ReadOnly, PnpMode.Allow));
            Assert.True(readOnly.Applied);
            int blocks = bus.BlockCalls;
            int unblocks = bus.UnblockCalls;

            Assert.Equal(0, enforcer.ReleaseEverything());

            // Off and on once: the drive comes back under the setting that is in force now.
            Assert.Equal(blocks + 1, bus.BlockCalls);
            Assert.Equal(unblocks + 1, bus.UnblockCalls);
            Assert.False(readOnly.Applied);
            Assert.False(Live(Devices.StickId).IsOff);
            Assert.True(Live(Devices.StickId).Started);
            Assert.Equal(0, ledger.Count);
        }

        [Fact]
        public void Release_everything_leaves_drives_alone_when_read_only_was_never_on()
        {
            bus.Plug(Devices.Stick());
            enforcer.RunOnce(Armed(StorageMode.Allow, PnpMode.Block));
            Assert.Equal(0, enforcer.ReleaseEverything());
            Assert.Equal(0, bus.BlockCalls);
        }

        [Fact]
        public void A_new_enforcer_picks_up_the_ledger_left_by_the_last_one()
        {
            bus.Plug(Devices.Headset());
            enforcer.RunOnce(Armed(StorageMode.Allow, PnpMode.Block));

            // The service restarts: new enforcer, same ledger on disk.
            Ledger reloaded = Ledger.FromJson(ledger.ToJson());
            var second = new Enforcer(bus, readOnly, reloaded, _ => { }, events.Add, () => now);

            PassResult stillArmed = second.RunOnce(Armed(StorageMode.Allow, PnpMode.Block));
            Assert.Equal(DeviceState.Blocked, StatusOf(stillArmed, Devices.HeadsetId).State);
            Assert.Equal(1, bus.BlockCalls);

            second.RunOnce(new Policy());
            Assert.False(Live(Devices.HeadsetId).IsOff);
        }
    }
}
