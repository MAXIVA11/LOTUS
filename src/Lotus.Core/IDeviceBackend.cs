using System.Collections.Generic;

namespace Lotus.Core
{
    public sealed class DeviceOpResult
    {
        public bool Ok;
        /// <summary>Windows has no record of the device any more, so there is nothing left to do.</summary>
        public bool Gone;
        /// <summary>Windows does not allow this device to be disabled. Keyboards are the usual case.</summary>
        public bool NotDisableable;
        /// <summary>A program had the device open, so Windows refused to stop it for now.</summary>
        public bool Vetoed;
        public string Message = "";

        public static DeviceOpResult Success()
        {
            return new DeviceOpResult { Ok = true };
        }

        public static DeviceOpResult Fail(string message)
        {
            return new DeviceOpResult { Ok = false, Message = message };
        }
    }

    /// <summary>How LOTUS switched a device off.</summary>
    public enum BlockMethod
    {
        /// <summary>Disabled like in Device Manager. The device stays off across replugging and restarts.</summary>
        Disabled,
        /// <summary>
        /// Removed from the running system, and Windows would not take a lasting
        /// disable on top. Windows forgets the removal when the device is plugged
        /// in again, so the service removes it again each time.
        /// </summary>
        Removed
    }

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
        /// <summary>One connected device, or null when the ID is not a connected USB device.</summary>
        UsbDevice ScanOne(string instanceId);
        /// <summary>Devices Windows remembers that are not connected right now.</summary>
        List<UsbDevice> ScanAbsent();
        /// <summary>
        /// Marks a device that is not connected as disabled, so that it is already
        /// off when it is plugged in.
        /// </summary>
        DeviceOpResult BlockAbsent(string instanceId);
        /// <summary>The functions of a connected device that Windows lists as device nodes of their own.</summary>
        List<UsbChild> Children(string instanceId);
        /// <summary>The device a function node belongs to, or null.</summary>
        string ParentOf(string instanceId);
        /// <summary>Makes Windows forget a connected device and find it again, so that it is installed afresh.</summary>
        DeviceOpResult Reinstall(string instanceId);
    }
}
