using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>USB devices as Windows shows them, and the Windows calls that switch them off and on.</summary>
    public sealed class WindowsDeviceBackend : IDeviceBackend, IReportsSystemDisk
    {
        /// <summary>
        /// Set when the last scan could not work out which disk Windows runs from.
        /// While it is set, storage devices are reported as system disks so that
        /// nothing risky gets disabled.
        /// </summary>
        public string SystemDiskError { get; private set; }

        // The answer from the last full scan. The single-device path uses it,
        // because working it out again for every arrival would cost too much time.
        private HashSet<string> systemAncestors;

        public List<UsbDevice> Scan()
        {
            List<UsbDevice> devices = DeviceScanner.Scan();
            MarkSystemDisk(devices);
            return devices;
        }

        public UsbDevice ScanOne(string instanceId)
        {
            UsbDevice device = DeviceScanner.ScanOne(instanceId);
            if (device == null) return null;
            if (!device.HasStorage && device.FunctionsKnown && !device.IsOff) return device;

            HashSet<string> ancestors = systemAncestors;
            if (ancestors == null)
            {
                try
                {
                    ancestors = SystemDisk.FindAncestors();
                    systemAncestors = ancestors;
                }
                catch (Exception)
                {
                    // Leave the decision to the full pass, which reports this problem.
                    if (device.HasStorage) device.IsSystemDisk = true;
                    return device;
                }
            }
            if (ancestors.Contains(device.InstanceId)) device.IsSystemDisk = true;
            return device;
        }

        public List<UsbDevice> ScanAbsent()
        {
            return DeviceScanner.ScanAbsent();
        }

        public DeviceOpResult BlockAbsent(string instanceId)
        {
            return DeviceControl.MarkAbsentDisabled(instanceId);
        }

        public List<UsbChild> Children(string instanceId)
        {
            return DeviceScanner.Children(instanceId);
        }

        public string ParentOf(string instanceId)
        {
            return DeviceScanner.ParentOf(instanceId);
        }

        public DeviceOpResult Reinstall(string instanceId)
        {
            DeviceScanner.Forget(instanceId);
            return DeviceControl.Reinstall(instanceId);
        }

        private void MarkSystemDisk(List<UsbDevice> devices)
        {
            bool anyStorage = false;
            foreach (UsbDevice device in devices)
            {
                if (device.HasStorage || !device.FunctionsKnown || device.IsOff) anyStorage = true;
            }
            if (!anyStorage)
            {
                SystemDiskError = null;
                return;
            }

            HashSet<string> ancestors;
            try
            {
                ancestors = SystemDisk.FindAncestors();
                systemAncestors = ancestors;
                SystemDiskError = null;
            }
            catch (Exception ex)
            {
                // Without this answer LOTUS cannot tell a USB boot disk from a flash
                // drive. Protect every storage device until the lookup works again.
                SystemDiskError = ex.Message;
                foreach (UsbDevice device in devices)
                {
                    if (device.HasStorage) device.IsSystemDisk = true;
                }
                return;
            }

            foreach (UsbDevice device in devices)
            {
                if (ancestors.Contains(device.InstanceId)) device.IsSystemDisk = true;
            }
        }

        public DeviceOpResult Block(string instanceId, out BlockMethod method)
        {
            return DeviceControl.Block(instanceId, out method);
        }

        public DeviceOpResult Unblock(string instanceId)
        {
            return DeviceControl.Unblock(instanceId);
        }
    }
}
