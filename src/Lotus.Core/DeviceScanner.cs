using System;
using System.Collections.Generic;
using Lotus.Core.Native;

namespace Lotus.Core
{
    /// <summary>
    /// Lists the devices plugged into USB ports right now. Reading the device tree
    /// needs no special rights, so the app can show the same list the service works from.
    /// </summary>
    public static class DeviceScanner
    {
        private static readonly HashSet<string> HubServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "usbhub", "usbhub3", "usbhub4", "iusb3hub", "nusb3hub", "asmthub3", "vusb3hub", "amdhub30",
            "rusb3hub", "viahub3", "etronhub3", "fl2000hub", "tihub3"
        };

        // What a device declares about itself does not change while it stays
        // plugged in, so it is read once per connection and remembered.
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, List<string>> DescriptorCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns every present USB device, including ones that are disabled.</summary>
        public static List<UsbDevice> Scan()
        {
            var result = new List<UsbDevice>();
            List<string> ids = CfgMgr.GetDeviceIds("USB",
                CfgMgr.CM_GETIDLIST_FILTER_ENUMERATOR | CfgMgr.CM_GETIDLIST_FILTER_PRESENT);

            foreach (string id in ids)
            {
                UsbDevice device = DescribePresent(id);
                if (device != null) result.Add(device);
            }

            ForgetAllExcept(ids);
            result.Sort((a, b) => string.Compare(a.InstanceId, b.InstanceId, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// Describes one connected device, or returns null when the ID is not a
        /// physical USB device that is plugged in right now. Much cheaper than a
        /// full scan, which is what makes it usable the instant a device appears.
        /// </summary>
        public static UsbDevice ScanOne(string instanceId)
        {
            return DescribePresent(instanceId);
        }

        private static UsbDevice DescribePresent(string id)
        {
            if (!UsbClassifier.ParseInstanceId(id, out string vid, out string pid, out string tail)) return null;
            if (CfgMgr.CM_Locate_DevNodeW(out int node, id, CfgMgr.CM_LOCATE_DEVNODE_NORMAL) != CfgMgr.CR_SUCCESS)
            {
                return null;
            }
            if (!IsPhysicalDevice(node)) return null;
            return Describe(node, id, vid, pid, tail, true);
        }

        /// <summary>
        /// Lists USB devices Windows remembers that are not plugged in right now.
        /// Only what Windows stored about them is available, so a composite device
        /// in this list does not say what it does.
        /// </summary>
        public static List<UsbDevice> ScanAbsent()
        {
            var result = new List<UsbDevice>();
            var present = new HashSet<string>(CfgMgr.GetDeviceIds("USB",
                CfgMgr.CM_GETIDLIST_FILTER_ENUMERATOR | CfgMgr.CM_GETIDLIST_FILTER_PRESENT),
                StringComparer.OrdinalIgnoreCase);

            foreach (string id in CfgMgr.GetDeviceIds("USB", CfgMgr.CM_GETIDLIST_FILTER_ENUMERATOR))
            {
                if (present.Contains(id) || !UsbClassifier.IsWholeDeviceId(id)) continue;
                if (!UsbClassifier.ParseInstanceId(id, out string vid, out string pid, out string tail)) continue;
                if (CfgMgr.CM_Locate_DevNodeW(out int node, id, CfgMgr.CM_LOCATE_DEVNODE_PHANTOM) !=
                    CfgMgr.CR_SUCCESS)
                {
                    continue;
                }
                UsbDevice device = Describe(node, id, vid, pid, tail, false);
                if (device == null) continue;
                device.Present = false;
                device.Started = false;
                device.Problem = CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CONFIGFLAGS, out int flags) &&
                                 (flags & CfgMgr.CONFIGFLAG_DISABLED) != 0
                    ? UsbDevice.ProblemDisabled
                    : 0;
                result.Add(device);
            }
            return result;
        }

        /// <summary>
        /// Drops what is remembered about one device. Called when Windows reports
        /// that the device appeared or left, because the same ID can come back as a
        /// different device.
        /// </summary>
        public static void Forget(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return;
            lock (CacheGate) DescriptorCache.Remove(instanceId);
        }

        private static void ForgetAllExcept(List<string> presentIds)
        {
            lock (CacheGate)
            {
                if (DescriptorCache.Count == 0) return;
                var present = new HashSet<string>(presentIds, StringComparer.OrdinalIgnoreCase);
                foreach (string id in new List<string>(DescriptorCache.Keys))
                {
                    if (!present.Contains(id)) DescriptorCache.Remove(id);
                }
            }
        }

        private static List<string> DescriptorIdsFor(int node, string id)
        {
            lock (CacheGate)
            {
                if (DescriptorCache.TryGetValue(id, out List<string> cached)) return cached;
            }
            UsbDescriptorInfo info = UsbDescriptors.Read(node);
            List<string> ids = info?.ToCompatibleIds();
            if (ids == null || ids.Count == 0) return null;
            lock (CacheGate) DescriptorCache[id] = ids;
            return ids;
        }

        /// <summary>
        /// A node under the USB enumerator is either a physical device on a hub port
        /// or one interface of a composite device. Interfaces hang off their device,
        /// physical devices hang off a hub.
        /// </summary>
        private static bool IsPhysicalDevice(int node)
        {
            if (CfgMgr.CM_Get_Parent(out int parent, node, 0) != CfgMgr.CR_SUCCESS) return true;
            string parentId = CfgMgr.GetDeviceId(parent) ?? "";
            if (!parentId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) return true;
            return IsHub(parent, parentId);
        }

        private static bool IsHub(int node, string id)
        {
            if (id.IndexOf("ROOT_HUB", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (HubServices.Contains(CfgMgr.GetString(node, CfgMgr.CM_DRP_SERVICE))) return true;
            foreach (string compatible in CfgMgr.GetMultiString(node, CfgMgr.CM_DRP_COMPATIBLEIDS))
            {
                string upper = compatible.ToUpperInvariant();
                if (upper.StartsWith(@"USB\CLASS_09", StringComparison.Ordinal) || upper == @"USB\USB20_HUB" ||
                    upper == @"USB\USB30_HUB")
                {
                    return true;
                }
            }
            return false;
        }

        private static UsbDevice Describe(int node, string id, string vid, string pid, string tail, bool present)
        {
            var device = new UsbDevice
            {
                InstanceId = id.ToUpperInvariant(),
                Vid = vid,
                Pid = pid,
                Present = true
            };

            if (CfgMgr.CM_Get_DevNode_Status(out int status, out int problem, node, 0) == CfgMgr.CR_SUCCESS)
            {
                device.Started = (status & CfgMgr.DN_STARTED) != 0;
                device.Problem = (status & CfgMgr.DN_HAS_PROBLEM) != 0 ? problem : 0;
                device.Disableable = (status & CfgMgr.DN_DISABLEABLE) != 0;
            }

            bool unique = CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CAPABILITIES, out int caps) &&
                          (caps & CfgMgr.CM_DEVCAP_UNIQUEID) != 0;
            device.HasSerial = unique && UsbClassifier.LooksLikeSerial(tail);
            device.Serial = device.HasSerial ? tail : "";

            // "Built in" exempts a device from the USB device rule, so it takes two
            // independent signs: Windows groups it with the computer itself, and the
            // port it sits in is not one a person can plug things into.
            bool removable = CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CAPABILITIES, out int capabilities) &&
                             (capabilities & CfgMgr.CM_DEVCAP_REMOVABLE) != 0;
            device.BuiltIn = !removable &&
                             CfgMgr.GetDevPropBool(node, CfgMgr.DEVPKEY_Device_InLocalMachineContainer) == true;
            device.Location = CfgMgr.GetString(node, CfgMgr.CM_DRP_LOCATION_INFORMATION);

            string service = CfgMgr.GetString(node, CfgMgr.CM_DRP_SERVICE);
            string className = CfgMgr.GetString(node, CfgMgr.CM_DRP_CLASS);
            var ids = new List<string>();
            ids.AddRange(CfgMgr.GetMultiString(node, CfgMgr.CM_DRP_HARDWAREID));
            ids.AddRange(CfgMgr.GetMultiString(node, CfgMgr.CM_DRP_COMPATIBLEIDS));

            bool hub = IsHub(node, id);
            var descendants = new List<DeviceNodeInfo>();
            if (!hub && present) CollectDescendants(node, descendants, 0);

            if (hub) ids.Add(@"USB\USB20_HUB");
            List<string> descriptorIds = hub || !present ? null : DescriptorIdsFor(node, id);
            UsbClassifier.Classify(device, className, service, ids, descendants, descriptorIds);
            device.Described = descriptorIds != null;

            // The device chooses these strings, so they are cleaned before anything else sees them.
            device.Name = TextSafety.Clean(PickName(node, descendants));
            if (device.Name.Length == 0) device.Name = "Unknown USB device";
            device.Vendor = TextSafety.Clean(PickVendor(node, vid), 48);
            return device;
        }

        private static void CollectDescendants(int node, List<DeviceNodeInfo> into, int depth)
        {
            if (depth > 12 || into.Count > 256) return;
            if (CfgMgr.CM_Get_Child(out int child, node, 0) != CfgMgr.CR_SUCCESS) return;
            int current = child;
            while (true)
            {
                string id = CfgMgr.GetDeviceId(current) ?? "";
                // Stop at the edge of the device. What hangs off another bus behind it
                // (devices paired with a Bluetooth adapter, for example) is not part of it.
                if (UsbClassifier.IsPartOfDevice(id))
                {
                    into.Add(new DeviceNodeInfo
                    {
                        InstanceId = id,
                        ClassName = CfgMgr.GetString(current, CfgMgr.CM_DRP_CLASS),
                        CompatibleIds = CfgMgr.GetMultiString(current, CfgMgr.CM_DRP_COMPATIBLEIDS),
                        FriendlyName = CfgMgr.GetString(current, CfgMgr.CM_DRP_FRIENDLYNAME)
                    });
                    CollectDescendants(current, into, depth + 1);
                }
                if (CfgMgr.CM_Get_Sibling(out int sibling, current, 0) != CfgMgr.CR_SUCCESS) break;
                current = sibling;
            }
        }

        private static string PickName(int node, List<DeviceNodeInfo> descendants)
        {
            // A disk's own name ("SanDisk Cruzer Blade USB Device") beats the generic
            // "USB Mass Storage Device" of the port it hangs off.
            foreach (DeviceNodeInfo info in descendants)
            {
                string cls = (info.ClassName ?? "").ToUpperInvariant();
                if ((cls == "DISKDRIVE" || cls == "CDROM" || cls == "WPD") && info.FriendlyName.Length > 0)
                {
                    return info.FriendlyName;
                }
            }

            string friendly = CfgMgr.GetString(node, CfgMgr.CM_DRP_FRIENDLYNAME);
            string description = CfgMgr.GetString(node, CfgMgr.CM_DRP_DEVICEDESC);
            string busName = CfgMgr.GetDevPropString(node, CfgMgr.DEVPKEY_Device_BusReportedDeviceDesc);
            string provider = CfgMgr.GetDevPropString(node, CfgMgr.DEVPKEY_Device_DriverProvider);

            if (friendly.Length > 0) return friendly;
            // A vendor's own driver usually names the device well. Windows' generic
            // drivers do not, so the name the device reports about itself is better.
            bool vendorDriver = provider.Length > 0 &&
                                !provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);
            if (vendorDriver && description.Length > 0) return description;
            if (busName.Length > 0) return busName;
            if (description.Length > 0) return description;
            return "Unknown USB device";
        }

        private static string PickVendor(int node, string vid)
        {
            string known = UsbVendors.Lookup(vid);
            if (known != null) return known;
            string manufacturer = CfgMgr.GetString(node, CfgMgr.CM_DRP_MFG);
            if (manufacturer.Length > 0 && manufacturer[0] != '(' &&
                !manufacturer.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                manufacturer.IndexOf("Compatible", StringComparison.OrdinalIgnoreCase) < 0 &&
                manufacturer.IndexOf("Generic", StringComparison.OrdinalIgnoreCase) < 0 &&
                manufacturer.IndexOf("WinUsb", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return manufacturer;
            }
            return "";
        }
    }

    /// <summary>A short table of common USB vendor IDs, so the device list reads better offline.</summary>
    public static class UsbVendors
    {
        private static readonly Dictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["03F0"] = "HP", ["0411"] = "Buffalo", ["041E"] = "Creative",
                ["0424"] = "Microchip", ["043E"] = "LG", ["045E"] = "Microsoft", ["046D"] = "Logitech",
                ["0480"] = "Toshiba", ["0483"] = "STMicroelectronics", ["04A9"] = "Canon", ["04B8"] = "Epson",
                ["04CA"] = "Lite-On", ["04D9"] = "Holtek", ["04E8"] = "Samsung", ["04F2"] = "Chicony",
                ["04F9"] = "Brother", ["0502"] = "Acer", ["050D"] = "Belkin",
                ["054C"] = "Sony", ["058F"] = "Alcor Micro", ["05AC"] = "Apple", ["05DC"] = "Lexar",
                ["05E3"] = "Genesys Logic", ["067B"] = "Prolific", ["06CB"] = "Synaptics",
                ["0781"] = "SanDisk", ["07CA"] = "AVerMedia", ["0846"] = "Netgear", ["08BB"] = "Texas Instruments",
                ["090C"] = "Silicon Motion", ["0930"] = "Toshiba", ["0951"] = "Kingston", ["0955"] = "NVIDIA",
                ["0A5C"] = "Broadcom", ["0B05"] = "ASUS", ["0B95"] = "ASIX", ["0BB4"] = "HTC",
                ["0BC2"] = "Seagate", ["0BDA"] = "Realtek", ["0C45"] = "Sonix", ["0D8C"] = "C-Media",
                ["0E8D"] = "MediaTek", ["0FCE"] = "Sony Mobile", ["1004"] = "LG", ["1038"] = "SteelSeries",
                ["1050"] = "Yubico", ["1058"] = "Western Digital", ["10C4"] = "Silicon Labs", ["1235"] = "Focusrite",
                ["12D1"] = "Huawei", ["13FE"] = "Kingston", ["1532"] = "Razer",
                ["154B"] = "PNY", ["174C"] = "ASMedia", ["17EF"] = "Lenovo", ["18A5"] = "Verbatim",
                ["18D1"] = "Google", ["1949"] = "Amazon", ["1A40"] = "Terminus",
                ["1A86"] = "QinHeng", ["1B1C"] = "Corsair", ["1BCF"] = "Sunplus", ["1D6B"] = "Linux Foundation",
                ["1F75"] = "Innostor", ["2109"] = "VIA Labs", ["22B8"] = "Motorola", ["22D9"] = "OPPO",
                ["2357"] = "TP-Link", ["2717"] = "Xiaomi", ["2833"] = "Oculus", ["28DE"] = "Valve",
                ["2A70"] = "OnePlus", ["2C7C"] = "Quectel", ["2E8A"] = "Raspberry Pi",
                ["8087"] = "Intel", ["8564"] = "Transcend"
            };

        public static string Lookup(string vid)
        {
            return vid != null && Names.TryGetValue(vid, out string name) ? name : null;
        }
    }
}
