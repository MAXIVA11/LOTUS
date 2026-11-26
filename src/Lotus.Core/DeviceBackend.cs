using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>
    /// What the enforcer needs from the operating system. The real implementation
    /// talks to Windows. Tests and demo mode plug in a simulated one.
    /// </summary>
    public interface IDeviceBackend
    {
        /// <summary>Every USB device that is connected right now, including ones that are switched off.</summary>
        List<UsbDevice> Scan();
        /// <summary>Switches a connected device off and reports which method worked.</summary>
        DeviceOpResult Block(string instanceId, out BlockMethod method);
        /// <summary>Switches a device back on, or clears its disabled mark when it is not connected.</summary>
        DeviceOpResult Unblock(string instanceId);
    }

    public sealed class WindowsDeviceBackend : IDeviceBackend
    {
        /// <summary>
        /// Set when the last scan could not work out which disk Windows runs from.
        /// While it is set, storage devices are reported as system disks so that
        /// nothing risky gets disabled.
        /// </summary>
        public string SystemDiskError { get; private set; }

        public List<UsbDevice> Scan()
        {
            List<UsbDevice> devices = DeviceScanner.Scan();
            MarkSystemDisk(devices);
            return devices;
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
