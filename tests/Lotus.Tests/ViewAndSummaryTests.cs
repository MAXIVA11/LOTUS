using System;
using Lotus.Core;
using Xunit;

namespace Lotus.Tests
{
    public class DeviceViewTests
    {
        private static Policy Armed(StorageMode storage, PnpMode pnp)
        {
            return new Policy { Armed = true, Storage = storage, Pnp = pnp };
        }

        [Fact]
        public void A_working_device_the_policy_blocks_shows_as_blocking()
        {
            DeviceStatus status = DeviceView.Resolve(Armed(StorageMode.Block, PnpMode.Allow), Devices.Stick(),
                new Ledger(), TimeSpan.MaxValue);
            Assert.Equal(DeviceState.Blocking, status.State);
        }

        [Fact]
        public void A_device_lotus_switched_off_shows_as_blocked_with_its_real_name()
        {
            var ledger = new Ledger();
            ledger.Add(LedgerEntry.From(Devices.Headset(), Reason.PnpBlocked, DateTime.UtcNow));
            var off = new UsbDevice
            {
                InstanceId = Devices.HeadsetId, Name = "USB Composite Device", Vid = "03F0", Pid = "0C9D",
                IsComposite = true, FunctionsKnown = false, Problem = UsbDevice.ProblemDisabled
            };

            DeviceStatus status = DeviceView.Resolve(Armed(StorageMode.Allow, PnpMode.Block), off, ledger,
                TimeSpan.MaxValue);

            Assert.Equal(DeviceState.Blocked, status.State);
            Assert.Equal("HyperX Cloud III Wireless", status.Device.Name);
            Assert.Equal(Reason.PnpBlocked, status.Decision.Reason);
        }

        [Fact]
        public void A_removed_keyboard_counts_as_switched_off()
        {
            var ledger = new Ledger();
            ledger.Add(LedgerEntry.From(Devices.Keyboard(), Reason.PnpBlocked, DateTime.UtcNow));
            UsbDevice removed = Devices.Keyboard();
            removed.Problem = UsbDevice.ProblemRemoved;
            removed.Started = false;

            var policy = new Policy { Armed = true, Pnp = PnpMode.Block, AllowInput = false };
            Assert.Equal(DeviceState.Blocked, DeviceView.Resolve(policy, removed, ledger, TimeSpan.MaxValue).State);
        }

        [Fact]
        public void A_device_switched_off_by_someone_else_is_reported_as_such()
        {
            UsbDevice off = Devices.Stick();
            off.Problem = UsbDevice.ProblemDisabled;
            DeviceStatus status = DeviceView.Resolve(Armed(StorageMode.Block, PnpMode.Block), off, new Ledger(),
                TimeSpan.MaxValue);
            Assert.Equal(DeviceState.OffElsewhere, status.State);
            Assert.False(status.Decision.IsBlock);
        }

        [Fact]
        public void A_blocked_device_that_became_trusted_still_shows_as_blocked_until_it_is_released()
        {
            var ledger = new Ledger();
            ledger.Add(LedgerEntry.From(Devices.Stick(), Reason.StorageBlocked, DateTime.UtcNow));
            UsbDevice off = Devices.Stick();
            off.Problem = UsbDevice.ProblemDisabled;
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            policy.AddTrust(TrustEntry.ForDevice(off));

            DeviceStatus status = DeviceView.Resolve(policy, off, ledger, TimeSpan.MaxValue);

            Assert.Equal(DeviceState.Blocked, status.State);
            Assert.Equal(Reason.Trusted, status.Decision.Reason);
        }

        [Fact]
        public void Working_devices_show_as_trusted_or_allowed()
        {
            Policy policy = Armed(StorageMode.Block, PnpMode.Allow);
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Assert.Equal(DeviceState.Trusted,
                DeviceView.Resolve(policy, Devices.Stick(), new Ledger(), TimeSpan.MaxValue).State);
            Assert.Equal(DeviceState.Allowed,
                DeviceView.Resolve(policy, Devices.Keyboard(), new Ledger(), TimeSpan.MaxValue).State);
        }
    }

    public class SummaryTests
    {
        [Fact]
        public void Disarmed_says_so_whatever_the_settings()
        {
            var policy = new Policy { Armed = false, Storage = StorageMode.Block, Pnp = PnpMode.Block };
            Assert.Equal("disarmed, nothing is blocked.", GuardWorker.Summarize(policy));
        }

        [Fact]
        public void Armed_lists_every_setting()
        {
            var policy = new Policy { Armed = true, Storage = StorageMode.ReadOnly, Pnp = PnpMode.Block, Strict = true };
            policy.AddTrust(TrustEntry.ForDevice(Devices.Stick()));
            Assert.Equal(
                "armed, removable storage read-only, other USB devices blocked unless trusted " +
                "(keyboards and mice stay allowed), new devices refused before they start, 1 trusted entry.",
                GuardWorker.Summarize(policy));

            policy.Strict = false;
            Assert.Equal(
                "armed, removable storage read-only, other USB devices blocked unless trusted " +
                "(keyboards and mice stay allowed), 1 trusted entry.",
                GuardWorker.Summarize(policy));

            Assert.Equal(
                "armed, removable storage blocked, other USB devices allowed, new storage devices refused " +
                "before they start, 0 trusted entries.",
                GuardWorker.Summarize(new Policy { Armed = true, Storage = StorageMode.Block, Strict = true }));
        }

        [Fact]
        public void Armed_with_everything_allowed()
        {
            var policy = new Policy { Armed = true };
            Assert.Equal("armed, removable storage allowed, other USB devices allowed, 0 trusted entries.",
                GuardWorker.Summarize(policy));
        }
    }

    public class SimulatedBusTests
    {
        [Fact]
        public void A_disabled_device_comes_back_disabled_when_replugged()
        {
            var bus = new SimulatedBackend();
            bus.Plug(Devices.Stick());
            bus.Block(Devices.StickId, out BlockMethod _);
            bus.Unplug(Devices.StickId);
            bus.Plug(Devices.Stick());
            Assert.True(bus.Scan()[0].IsOff);

            bus.Unblock(Devices.StickId);
            Assert.False(bus.Scan()[0].IsOff);
        }

        [Fact]
        public void A_composite_device_forgets_its_interfaces_while_off()
        {
            var bus = new SimulatedBackend();
            bus.Plug(Devices.Headset());
            bus.Block(Devices.HeadsetId, out BlockMethod _);
            UsbDevice off = bus.Scan()[0];
            Assert.False(off.FunctionsKnown);
            Assert.Equal(DeviceKinds.None, off.Kinds);

            bus.Unblock(Devices.HeadsetId);
            Assert.Equal(DeviceKinds.Audio | DeviceKinds.Input, bus.Scan()[0].Kinds);
        }
    }
}
