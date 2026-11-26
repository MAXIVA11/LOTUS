using System.Collections.Generic;
using Lotus.Core;

namespace Lotus.Tests
{
    /// <summary>Sample devices shared by the tests.</summary>
    internal static class Devices
    {
        public const string StickId = @"USB\VID_0781&PID_5567\4C530001230505116353";
        public const string KeyboardId = @"USB\VID_1A2C&PID_2D23\5&CC3F949&0&6";
        public const string HeadsetId = @"USB\VID_03F0&PID_0C9D\0000000000000000";
        public const string PhoneId = @"USB\VID_18D1&PID_4EE1\0A1B2C3D4E5F";
        public const string HubId = @"USB\VID_05E3&PID_0608\5&CC3F949&0&3";
        public const string WebcamId = @"USB\VID_04F2&PID_B6D9\0001";
        public const string ComboId = @"USB\VID_1234&PID_5678\6&AAAA&0&2";
        public const string MouseId = @"USB\VID_046D&PID_C542\5&CC3F949&0&5";

        public static UsbDevice Stick(string id = StickId)
        {
            return new UsbDevice
            {
                InstanceId = id, Name = "SanDisk Cruzer Blade USB Device", Vendor = "SanDisk",
                Vid = "0781", Pid = "5567", Serial = "4C530001230505116353", HasSerial = true,
                Functions = new List<string> { "08" }, Kinds = DeviceKinds.Storage, Started = true
            };
        }

        public static UsbDevice Keyboard()
        {
            return new UsbDevice
            {
                InstanceId = KeyboardId, Name = "USB Keyboard", Vid = "1A2C", Pid = "2D23",
                IsComposite = true, Functions = new List<string> { "03" }, Kinds = DeviceKinds.Input,
                Started = true, Disableable = false
            };
        }

        public static UsbDevice Mouse()
        {
            return new UsbDevice
            {
                InstanceId = MouseId, Name = "Wireless Receiver", Vendor = "Logitech", Vid = "046D", Pid = "C542",
                Functions = new List<string> { "03" }, Kinds = DeviceKinds.Input, Started = true
            };
        }

        public static UsbDevice Headset()
        {
            return new UsbDevice
            {
                InstanceId = HeadsetId, Name = "HyperX Cloud III Wireless", Vendor = "HP", Vid = "03F0",
                Pid = "0C9D", Serial = "0000000000000000", HasSerial = true, IsComposite = true,
                Functions = new List<string> { "01", "03" }, Kinds = DeviceKinds.Audio | DeviceKinds.Input,
                Started = true
            };
        }

        public static UsbDevice Phone()
        {
            return new UsbDevice
            {
                InstanceId = PhoneId, Name = "Pixel 8", Vendor = "Google", Vid = "18D1", Pid = "4EE1",
                Serial = "0A1B2C3D4E5F", HasSerial = true, Functions = new List<string> { "MTP" },
                Kinds = DeviceKinds.Portable, Started = true
            };
        }

        public static UsbDevice Hub()
        {
            return new UsbDevice
            {
                InstanceId = HubId, Name = "Generic USB Hub", Vendor = "Genesys Logic", Vid = "05E3", Pid = "0608",
                IsHub = true, Functions = new List<string> { "09" }, Kinds = DeviceKinds.Hub, Started = true
            };
        }

        public static UsbDevice BuiltInWebcam()
        {
            return new UsbDevice
            {
                InstanceId = WebcamId, Name = "Integrated Camera", Vendor = "Chicony", Vid = "04F2", Pid = "B6D9",
                Serial = "0001", HasSerial = true, BuiltIn = true, Functions = new List<string> { "0E" },
                Kinds = DeviceKinds.Video, Started = true
            };
        }

        /// <summary>A stick that also pretends to be a keyboard, the classic attack device.</summary>
        public static UsbDevice StorageKeyboardCombo()
        {
            return new UsbDevice
            {
                InstanceId = ComboId, Name = "Flash Disk", Vid = "1234", Pid = "5678", IsComposite = true,
                Functions = new List<string> { "08", "03" }, Kinds = DeviceKinds.Storage | DeviceKinds.Input,
                Started = true
            };
        }

        /// <summary>A composite device in the first moment after plugging in, before its interfaces appear.</summary>
        public static UsbDevice FreshComposite()
        {
            return new UsbDevice
            {
                InstanceId = ComboId, Name = "USB Composite Device", Vid = "1234", Pid = "5678",
                IsComposite = true, FunctionsKnown = false, Kinds = DeviceKinds.None, Started = true
            };
        }
    }
}
