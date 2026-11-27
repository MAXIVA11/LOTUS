using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>What a USB device can do, worked out from its USB class codes and drivers.</summary>
    [Flags]
    public enum DeviceKinds
    {
        None = 0,
        /// <summary>Mass storage: flash drives, external disks, card readers, optical drives.</summary>
        Storage = 1,
        /// <summary>Phones, cameras and media players that share files over MTP or PTP.</summary>
        Portable = 2,
        /// <summary>Keyboards, mice, game controllers and other HID devices.</summary>
        Input = 4,
        Audio = 8,
        Video = 16,
        /// <summary>Network adapters, modems and serial ports.</summary>
        Network = 32,
        Bluetooth = 64,
        Printer = 128,
        SmartCard = 256,
        Hub = 512,
        Other = 1024
    }

    /// <summary>
    /// One device plugged into a USB port, as LOTUS sees it. A composite device
    /// (a headset with audio and buttons, say) is still one entry: LOTUS always
    /// decides for the whole physical device.
    /// </summary>
    public sealed class UsbDevice
    {
        /// <summary>Windows device instance ID, for example USB\VID_0781&amp;PID_5567\4C5300012305.</summary>
        public string InstanceId = "";
        public string Name = "";
        public string Vendor = "";
        /// <summary>Four hex digits, upper case.</summary>
        public string Vid = "";
        /// <summary>Four hex digits, upper case.</summary>
        public string Pid = "";
        /// <summary>The serial number the device reports, or empty when it has none.</summary>
        public string Serial = "";
        /// <summary>
        /// False when Windows had to invent the last part of the instance ID. Such a
        /// device gets a new ID when it moves to another port.
        /// </summary>
        public bool HasSerial;
        /// <summary>Windows reports the device as part of the computer itself.</summary>
        public bool BuiltIn;
        public bool Present = true;
        public bool Started;
        /// <summary>Device Manager problem code. 0 means no problem.</summary>
        public int Problem;
        public bool Disableable = true;
        public bool IsHub;
        public bool IsComposite;
        /// <summary>Windows itself runs from a disk behind this device, so it is never blocked.</summary>
        public bool IsSystemDisk;
        /// <summary>USB class codes as two hex digits, plus "MTP" for media transfer devices.</summary>
        public List<string> Functions = new List<string>();
        /// <summary>
        /// False while a composite device has not listed its functions yet, which is
        /// the case for a moment after it is plugged in and for as long as it is disabled.
        /// </summary>
        public bool FunctionsKnown = true;
        /// <summary>
        /// The functions were read from the device's own descriptors, so they are
        /// right even while the device is switched off.
        /// </summary>
        public bool Described;
        public DeviceKinds Kinds;
        public string Location = "";

        /// <summary>Device Manager code 22: the device is disabled.</summary>
        public const int ProblemDisabled = 22;
        /// <summary>Device Manager code 21: the device was removed from the running system.</summary>
        public const int ProblemRemoved = 21;

        /// <summary>The device is plugged in but switched off, by LOTUS or by someone else.</summary>
        public bool IsOff => Problem == ProblemDisabled || Problem == ProblemRemoved;

        /// <summary>Windows problem code of a device that has no driver set up yet.</summary>
        public const int ProblemNotConfigured = 1;
        /// <summary>Windows problem code of a device whose installation did not go through.</summary>
        public const int ProblemFailedInstall = 28;

        /// <summary>
        /// Connected, but Windows has not installed it. That is how a device looks
        /// that Windows refused under the installation restrictions.
        /// </summary>
        public bool InstallRefused => Present && !Started && IsRefusedProblem(Problem);

        public static bool IsRefusedProblem(int problem)
        {
            return problem == ProblemNotConfigured || problem == ProblemFailedInstall;
        }

        /// <summary>Vendor and product, for example VID_0781&amp;PID_5567.</summary>
        public string ModelId => "VID_" + Vid + "&PID_" + Pid;

        public bool HasStorage => (Kinds & (DeviceKinds.Storage | DeviceKinds.Portable)) != 0;

        /// <summary>Everything the device does besides storing files.</summary>
        public DeviceKinds NonStorageKinds => Kinds & ~(DeviceKinds.Storage | DeviceKinds.Portable);

        public UsbDevice Clone()
        {
            var copy = (UsbDevice)MemberwiseClone();
            copy.Functions = new List<string>(Functions);
            return copy;
        }

        public override string ToString()
        {
            return Name + " [" + InstanceId + "]";
        }
    }

    /// <summary>One function of a USB device that Windows lists as a device node of its own.</summary>
    public sealed class UsbChild
    {
        public string InstanceId = "";
        public bool Started;
        public int Problem;

        public bool InstallRefused => !Started && UsbDevice.IsRefusedProblem(Problem);
    }
}
