using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Lotus.Core
{
    /// <summary>A child node of a USB device: one interface of a composite device, a disk, a HID collection.</summary>
    public sealed class DeviceNodeInfo
    {
        public string InstanceId = "";
        /// <summary>Windows setup class name, for example DiskDrive or HIDClass.</summary>
        public string ClassName = "";
        public List<string> CompatibleIds = new List<string>();
        public string FriendlyName = "";
    }

    /// <summary>
    /// Works out what a USB device does. The USB bus driver publishes each
    /// interface's class code as a compatible ID such as USB\Class_08&amp;SubClass_06,
    /// and the installed drivers add a setup class such as DiskDrive. Both are read here.
    /// </summary>
    public static class UsbClassifier
    {
        private static readonly Regex ClassCode =
            new Regex(@"^USB\\CLASS_([0-9A-F]{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex VidPid =
            new Regex(@"^USB\\VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Fills in Functions, Kinds, IsHub, IsComposite and FunctionsKnown.
        /// </summary>
        /// <param name="rootClass">Setup class of the device itself.</param>
        /// <param name="rootService">Driver service of the device itself, for example usbccgp.</param>
        /// <param name="rootIds">Hardware and compatible IDs of the device itself.</param>
        /// <param name="descendants">Every node below the device, excluding devices behind a hub.</param>
        public static void Classify(UsbDevice device, string rootClass, string rootService,
            IEnumerable<string> rootIds, IEnumerable<DeviceNodeInfo> descendants)
        {
            var functions = new List<string>();
            DeviceKinds kinds = DeviceKinds.None;
            bool composite = string.Equals(rootService, "usbccgp", StringComparison.OrdinalIgnoreCase);
            bool hub = false;

            var rootUpper = new List<string>();
            foreach (string id in rootIds ?? new string[0])
            {
                if (!string.IsNullOrEmpty(id)) rootUpper.Add(id.ToUpperInvariant());
            }

            foreach (string id in rootUpper)
            {
                if (id == @"USB\COMPOSITE") composite = true;
                if (id.Contains("ROOT_HUB") || id == @"USB\USB20_HUB" || id == @"USB\USB30_HUB") hub = true;
            }

            AddFromIds(rootUpper, functions, ref kinds);
            kinds |= KindFromSetupClass(rootClass);

            int usbChildren = 0;
            foreach (DeviceNodeInfo node in descendants ?? new DeviceNodeInfo[0])
            {
                string enumerator = EnumeratorOf(node.InstanceId);
                if (enumerator == "USB")
                {
                    // One interface of a composite device.
                    var upper = new List<string>();
                    foreach (string id in node.CompatibleIds)
                    {
                        if (!string.IsNullOrEmpty(id)) upper.Add(id.ToUpperInvariant());
                    }
                    usbChildren++;
                    AddFromIds(upper, functions, ref kinds);
                    kinds |= KindFromSetupClass(node.ClassName);
                }
                else if (enumerator == "USBSTOR" || enumerator == "SCSI")
                {
                    // A disk or optical drive behind a storage interface.
                    kinds |= KindFromSetupClass(node.ClassName) & DeviceKinds.Storage;
                }
                // Anything else sits on another bus behind this device, such as the
                // phones and keyboards paired with a Bluetooth adapter. Those are
                // separate devices and say nothing about what this one is.
            }

            if (functions.Contains("09")) hub = true;
            if (hub)
            {
                // A hub is only a hub. Whatever is plugged into it is judged separately.
                kinds = DeviceKinds.Hub;
                composite = false;
            }

            device.IsHub = hub;
            device.IsComposite = composite;
            device.Functions = functions;
            // A composite device says nothing about itself until its interfaces show up.
            device.FunctionsKnown = !(composite && usbChildren == 0);
            if (device.FunctionsKnown && kinds == DeviceKinds.None) kinds = DeviceKinds.Other;
            device.Kinds = kinds;
        }

        /// <summary>The bus a node sits on: the part of its instance ID before the first backslash.</summary>
        public static string EnumeratorOf(string instanceId)
        {
            string id = instanceId ?? "";
            int slash = id.IndexOf('\\');
            return (slash < 0 ? id : id.Substring(0, slash)).ToUpperInvariant();
        }

        /// <summary>
        /// True for nodes that are part of the USB device itself: its interfaces and
        /// the disks behind a storage interface.
        /// </summary>
        public static bool IsPartOfDevice(string instanceId)
        {
            string enumerator = EnumeratorOf(instanceId);
            return enumerator == "USB" || enumerator == "USBSTOR" || enumerator == "SCSI";
        }

        private static void AddFromIds(List<string> upperIds, List<string> functions, ref DeviceKinds kinds)
        {
            foreach (string id in upperIds)
            {
                if (id == @"USB\MS_COMP_MTP")
                {
                    AddFunction(functions, "MTP", ref kinds);
                    continue;
                }
                Match match = ClassCode.Match(id);
                if (!match.Success) continue;
                string code = match.Groups[1].Value;
                if (code == "E0")
                {
                    // E0 is "wireless controller". Subclass 01 protocol 01 is Bluetooth.
                    // Anything else in this class is a wireless network adapter.
                    if (!functions.Contains("E0")) functions.Add("E0");
                    if (id.StartsWith(@"USB\CLASS_E0&SUBCLASS_01&PROT_01", StringComparison.Ordinal))
                    {
                        kinds |= DeviceKinds.Bluetooth;
                    }
                    else if (id.Contains("&PROT_"))
                    {
                        kinds |= DeviceKinds.Network;
                    }
                    continue;
                }
                AddFunction(functions, code, ref kinds);
            }
            if (functions.Contains("E0") && (kinds & (DeviceKinds.Bluetooth | DeviceKinds.Network)) == 0)
            {
                kinds |= DeviceKinds.Other;
            }
        }

        private static void AddFunction(List<string> functions, string code, ref DeviceKinds kinds)
        {
            if (!functions.Contains(code)) functions.Add(code);
            kinds |= KindFromFunction(code);
        }

        /// <summary>Maps a USB class code to what the interface does.</summary>
        public static DeviceKinds KindFromFunction(string code)
        {
            switch (code)
            {
                case "01": return DeviceKinds.Audio;
                case "02": return DeviceKinds.Network;   // communications: modems, network, serial
                case "0A": return DeviceKinds.Network;   // CDC data
                case "03": return DeviceKinds.Input;     // HID
                case "06": return DeviceKinds.Portable;  // still image, used by PTP and MTP
                case "MTP": return DeviceKinds.Portable;
                case "07": return DeviceKinds.Printer;
                case "08": return DeviceKinds.Storage;   // mass storage, including UAS
                case "09": return DeviceKinds.Hub;
                case "0B": return DeviceKinds.SmartCard;
                case "0E": return DeviceKinds.Video;
                case "10": return DeviceKinds.Video;     // audio/video devices
                default: return DeviceKinds.Other;       // vendor specific, diagnostic, misc
            }
        }

        /// <summary>Maps a Windows setup class to what the driver exposes.</summary>
        public static DeviceKinds KindFromSetupClass(string className)
        {
            switch ((className ?? "").ToUpperInvariant())
            {
                case "DISKDRIVE":
                case "CDROM":
                case "FLOPPYDISK":
                case "TAPEDRIVE":
                    return DeviceKinds.Storage;
                case "WPD":
                    return DeviceKinds.Portable;
                case "KEYBOARD":
                case "MOUSE":
                case "HIDCLASS":
                    return DeviceKinds.Input;
                case "MEDIA":
                case "AUDIOENDPOINT":
                    return DeviceKinds.Audio;
                case "CAMERA":
                case "IMAGE":
                    return DeviceKinds.Video;
                case "NET":
                case "PORTS":
                case "MODEM":
                    return DeviceKinds.Network;
                case "BLUETOOTH":
                    return DeviceKinds.Bluetooth;
                case "PRINTER":
                case "PRINTQUEUE":
                    return DeviceKinds.Printer;
                case "SMARTCARDREADER":
                    return DeviceKinds.SmartCard;
                default:
                    return DeviceKinds.None;
            }
        }

        /// <summary>
        /// Splits a USB instance ID into vendor, product and serial number.
        /// Returns false when the ID does not describe a USB device.
        /// </summary>
        public static bool ParseInstanceId(string instanceId, out string vid, out string pid, out string tail)
        {
            vid = pid = tail = "";
            string upper = (instanceId ?? "").ToUpperInvariant();
            Match match = VidPid.Match(upper);
            if (!match.Success) return false;
            vid = match.Groups[1].Value;
            pid = match.Groups[2].Value;
            int slash = upper.LastIndexOf('\\');
            tail = slash >= 0 && slash < upper.Length - 1 ? upper.Substring(slash + 1) : "";
            return true;
        }

        /// <summary>
        /// Windows builds the last part of an instance ID itself when a device has no
        /// serial number. Those generated IDs always have '&amp;' as their second character.
        /// </summary>
        public static bool LooksLikeSerial(string tail)
        {
            if (string.IsNullOrEmpty(tail)) return false;
            return !(tail.Length > 1 && tail[1] == '&');
        }

        /// <summary>A short label for the most important thing a device does.</summary>
        public static string PrimaryLabel(DeviceKinds kinds)
        {
            if ((kinds & DeviceKinds.Storage) != 0) return "Storage";
            if ((kinds & DeviceKinds.Portable) != 0) return "Phone or camera";
            if ((kinds & DeviceKinds.Hub) != 0) return "USB hub";
            if ((kinds & DeviceKinds.Bluetooth) != 0) return "Bluetooth";
            if ((kinds & DeviceKinds.Network) != 0) return "Network";
            if ((kinds & DeviceKinds.Audio) != 0) return "Audio";
            if ((kinds & DeviceKinds.Video) != 0) return "Camera";
            if ((kinds & DeviceKinds.Printer) != 0) return "Printer";
            if ((kinds & DeviceKinds.SmartCard) != 0) return "Smart card";
            if ((kinds & DeviceKinds.Input) != 0) return "Input";
            return "Other";
        }

        /// <summary>Every label that applies, most important first.</summary>
        public static List<string> Labels(DeviceKinds kinds)
        {
            var labels = new List<string>();
            if ((kinds & DeviceKinds.Storage) != 0) labels.Add("Storage");
            if ((kinds & DeviceKinds.Portable) != 0) labels.Add("Phone or camera");
            if ((kinds & DeviceKinds.Hub) != 0) labels.Add("USB hub");
            if ((kinds & DeviceKinds.Input) != 0) labels.Add("Input");
            if ((kinds & DeviceKinds.Audio) != 0) labels.Add("Audio");
            if ((kinds & DeviceKinds.Video) != 0) labels.Add("Camera");
            if ((kinds & DeviceKinds.Bluetooth) != 0) labels.Add("Bluetooth");
            if ((kinds & DeviceKinds.Network) != 0) labels.Add("Network");
            if ((kinds & DeviceKinds.Printer) != 0) labels.Add("Printer");
            if ((kinds & DeviceKinds.SmartCard) != 0) labels.Add("Smart card");
            if ((kinds & DeviceKinds.Other) != 0 || labels.Count == 0) labels.Add("Other");
            return labels;
        }
    }
}
