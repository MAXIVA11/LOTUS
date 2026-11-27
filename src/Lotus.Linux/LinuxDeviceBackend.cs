using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Lotus.Core;

namespace Lotus.Linux
{
    /// <summary>
    /// USB devices as the Linux kernel shows them under /sys/bus/usb/devices, and
    /// the kernel's own switch for each of them: the "authorized" file. A device
    /// that is not authorized has no configuration, so no driver is bound to it
    /// and none of its functions exist. Writing 0 takes a running device down at
    /// once, and writing 1 brings it up.
    ///
    /// The kernel reads a device's descriptors before it asks whether the device
    /// is authorized, and keeps them readable, so LOTUS knows what a device is
    /// without letting it run.
    /// </summary>
    public sealed class LinuxDeviceBackend : IDeviceBackend, IAdmitsAtOnce, IReportsSystemDisk
    {
        public const string DefaultRoot = "/sys/bus/usb/devices";

        private readonly string root;
        private readonly Func<HashSet<string>> findSystemDevices;

        /// <summary>
        /// Says whether LOTUS itself switched a device off. The kernel shows a
        /// device that is off in one way only, whoever did it. A device LOTUS
        /// did not switch off is waiting to be let in, or was left off by the
        /// kernel's own rule, and counts as "not started yet".
        /// </summary>
        public Func<string, bool> IsBlockedByLotus = id => false;

        /// <summary>
        /// Set when the last scan could not work out which devices the system
        /// runs from. While it is set, storage devices are left alone.
        /// </summary>
        public string SystemDiskError { get; private set; }
        private readonly object gate = new object();
        /// <summary>LOTUS's ID of each connected device, and the kernel's name for it.</summary>
        private Dictionary<string, string> kernelNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <param name="root">Tests point this at a made-up tree.</param>
        /// <param name="findSystemDevices">Tests give their own answer to "which devices does the system run from".</param>
        public LinuxDeviceBackend(string root = DefaultRoot, Func<HashSet<string>> findSystemDevices = null)
        {
            this.root = root;
            this.findSystemDevices = findSystemDevices ??
                (root == DefaultRoot ? (Func<HashSet<string>>)(() => LinuxSystemDisk.Find()) : () => new HashSet<string>());
        }

        /// <summary>The USB controllers, as the kernel names them: usb1, usb2 and so on.</summary>
        public List<string> Controllers()
        {
            var names = new List<string>();
            if (!Directory.Exists(root)) return names;
            foreach (string path in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("usb", StringComparison.Ordinal) && IsDigits(name.Substring(3))) names.Add(name);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        public string ControllerFile(string controller, string file)
        {
            return Path.Combine(root, controller, file);
        }

        /// <summary>Runs before every look at the devices. The controller gate uses it to notice a controller that came later.</summary>
        public Action BeforeScan;

        /// <summary>The kernel names of the devices whose descriptors this backend has read while they were connected.</summary>
        private readonly HashSet<string> hadDescriptors = new HashSet<string>(StringComparer.Ordinal);

        public List<UsbDevice> Scan()
        {
            BeforeScan?.Invoke();
            var devices = new List<UsbDevice>();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> systemDevices = null;
            try
            {
                systemDevices = findSystemDevices();
                SystemDiskError = null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SystemDiskError = ex.Message;
            }
            if (Directory.Exists(root))
            {
                var entries = new List<string>();
                foreach (string path in Directory.GetDirectories(root)) entries.Add(Path.GetFileName(path));
                // Shortest name first: a hub comes before what is plugged into it.
                entries.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b));
                foreach (string name in entries)
                {
                    if (!IsDeviceName(name)) continue;
                    UsbDevice device = Describe(name, names);
                    if (device == null) continue;
                    // Without the answer nothing that could be the system's own disk is touched.
                    device.IsSystemDisk = systemDevices != null ? systemDevices.Contains(name) : device.HasStorage;
                    names[device.InstanceId] = name;
                    devices.Add(device);
                }
            }
            lock (gate)
            {
                kernelNames = names;
                // A port that is empty now starts afresh with whatever is plugged in next.
                hadDescriptors.RemoveWhere(name => !Directory.Exists(Path.Combine(root, name)));
            }
            devices.Sort((a, b) => string.Compare(a.InstanceId, b.InstanceId, StringComparison.OrdinalIgnoreCase));
            return devices;
        }

        /// <summary>
        /// The USB disks the system runs from, in the terms a udev rule can match
        /// them by. Hubs on the way to such a disk are left out: naming a hub
        /// would cover every disk behind it. Throws when the system's disks
        /// cannot be worked out.
        /// </summary>
        public List<UsbIdentity> SystemDisks()
        {
            var disks = new List<UsbIdentity>();
            foreach (string name in findSystemDevices())
            {
                string dir = Path.Combine(root, name);
                if ((ReadLine(Path.Combine(dir, "bDeviceClass")) ?? "").Trim() == "09") continue;
                string vid = ReadHex4(Path.Combine(dir, "idVendor"));
                string pid = ReadHex4(Path.Combine(dir, "idProduct"));
                if (vid == null || pid == null) continue;
                disks.Add(new UsbIdentity
                {
                    Vid = vid.ToLowerInvariant(),
                    Pid = pid.ToLowerInvariant(),
                    RawSerial = (ReadLine(Path.Combine(dir, "serial")) ?? "").Trim()
                });
            }
            return disks;
        }

        public UsbDevice ScanOne(string instanceId)
        {
            foreach (UsbDevice device in Scan())
            {
                if (device.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) return device;
            }
            return null;
        }

        /// <summary>
        /// "1-1" and "1-1.4.2" are devices: bus, then the chain of ports. "usb1" is
        /// a controller and "1-1:1.0" is one function of a device.
        /// </summary>
        public static bool IsDeviceName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf(':') >= 0) return false;
            int dash = name.IndexOf('-');
            if (dash <= 0 || dash == name.Length - 1) return false;
            if (!IsDigits(name.Substring(0, dash))) return false;
            foreach (string part in name.Substring(dash + 1).Split('.'))
            {
                if (!IsDigits(part)) return false;
            }
            return true;
        }

        private static bool IsDigits(string text)
        {
            if (text.Length == 0) return false;
            foreach (char c in text)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        /// <summary>
        /// One device, or null when the folder is not a device or the device is
        /// on its way out. While the kernel takes a device away its folder is
        /// still listed for a moment, but its files can no longer be read. Half
        /// of a description is worse than none: a keyboard without its
        /// descriptors would look like an unknown device and be judged as one.
        /// </summary>
        private UsbDevice Describe(string name, Dictionary<string, string> taken)
        {
            try
            {
                return DescribeWhole(name, taken);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private UsbDevice DescribeWhole(string name, Dictionary<string, string> taken)
        {
            string dir = Path.Combine(root, name);
            string vid = ReadHex4(Path.Combine(dir, "idVendor"));
            string pid = ReadHex4(Path.Combine(dir, "idProduct"));
            if (vid == null || pid == null) return null;

            string serial = CleanSerial(ReadLine(Path.Combine(dir, "serial")));
            string product = TextSafety.Clean(ReadLine(Path.Combine(dir, "product")) ?? "");
            string maker = TextSafety.Clean(ReadLine(Path.Combine(dir, "manufacturer")) ?? "");

            var device = new UsbDevice
            {
                Vid = vid,
                Pid = pid,
                Vendor = maker,
                Name = product.Length > 0 ? product : (maker.Length > 0 ? maker + " device" : "USB device"),
                Location = name,
                Present = true,
                Disableable = true
            };

            // The serial number makes the ID follow the device to any port. Without
            // one, or when another connected device shows the same one, the port
            // is the only thing that tells two devices of a model apart.
            string model = @"USB\VID_" + vid + "&PID_" + pid + @"\";
            string id = serial.Length > 0 ? model + serial : null;
            if (id == null || taken.ContainsKey(id))
            {
                id = model + "P&" + name.ToUpperInvariant();
                device.HasSerial = false;
                device.Serial = "";
            }
            else
            {
                device.HasSerial = true;
                device.Serial = serial;
            }
            device.InstanceId = Policy.NormalizeId(id);

            bool described = ReadFunctions(dir, device);
            lock (gate)
            {
                if (described) hadDescriptors.Add(name);
                // The kernel takes the descriptors away a moment before the folder.
                // A device that had them and has none now is on its way out. Judged
                // without them, a keyboard would count as an unknown device.
                else if (hadDescriptors.Contains(name)) return null;
            }

            // "fixed" means the port is inside the computer: a laptop's webcam or keyboard.
            string removable = (ReadLine(Path.Combine(dir, "removable")) ?? "").Trim();
            device.BuiltIn = removable == "fixed";

            string authorized = (ReadLine(Path.Combine(dir, "authorized")) ?? "1").Trim();
            if (authorized == "0")
            {
                device.Started = false;
                device.Problem = IsBlockedByLotus(device.InstanceId)
                    ? UsbDevice.ProblemDisabled
                    : UsbDevice.ProblemNotConfigured;
            }
            else
            {
                device.Started = true;
                device.Problem = 0;
            }
            // Gone while it was being read.
            return Directory.Exists(dir) ? device : null;
        }

        /// <summary>
        /// What the device does, from its own descriptors: the device descriptor
        /// first, then every configuration it offers. All configurations count,
        /// because a device can be switched to another one later.
        /// </summary>
        /// <returns>True when the device's own descriptors could be read, false when only the class file was there, or nothing.</returns>
        private static bool ReadFunctions(string dir, UsbDevice device)
        {
            byte[] raw = ReadBytes(Path.Combine(dir, "descriptors"));
            var interfaces = new List<UsbInterfaceInfo>();
            int deviceClass = -1;
            bool mediaTransfer = false;
            bool fromDescriptors = raw != null && raw.Length >= 18 && raw[0] >= 18 && raw[1] == 1;
            if (fromDescriptors)
            {
                deviceClass = raw[4];
                int offset = raw[0];
                while (offset + 4 <= raw.Length)
                {
                    int length = raw[offset];
                    int type = raw[offset + 1];
                    int total = raw[offset + 2] | raw[offset + 3] << 8;
                    if (length < 4 || type != 2 || total < length) break;
                    if (offset + total > raw.Length) total = raw.Length - offset;
                    var configuration = new byte[total];
                    Buffer.BlockCopy(raw, offset, configuration, 0, total);
                    UsbDescriptors.ParseConfiguration(configuration, interfaces);
                    if (LooksLikeMediaTransfer(configuration)) mediaTransfer = true;
                    offset += total;
                }
            }
            else
            {
                string text = ReadLine(Path.Combine(dir, "bDeviceClass"));
                if (text != null && int.TryParse(text.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
                {
                    deviceClass = value;
                }
            }

            var functions = new List<string>();
            DeviceKinds kinds = DeviceKinds.None;
            foreach (UsbInterfaceInfo info in interfaces)
            {
                string code = info.Class.ToString("X2");
                if (!functions.Contains(code)) functions.Add(code);
                kinds |= UsbClassifier.KindFromFunction(code);
            }
            if (mediaTransfer)
            {
                functions.Add("MTP");
                kinds |= UsbClassifier.KindFromFunction("MTP");
            }
            // A class in the device descriptor describes the device as a whole.
            // 00 and EF only say "look at the interfaces".
            if (deviceClass > 0 && deviceClass != 0xEF)
            {
                string code = deviceClass.ToString("X2");
                if (!functions.Contains(code)) functions.Add(code);
                kinds |= UsbClassifier.KindFromFunction(code);
            }

            device.Functions = functions;
            device.Kinds = kinds;
            device.IsHub = deviceClass == 9 || functions.Contains("09");
            device.IsComposite = interfaces.Count > 1;
            // Without descriptors nothing is known, and the policy treats the device accordingly.
            device.FunctionsKnown = functions.Count > 0;
            device.Described = functions.Count > 0;
            return fromDescriptors;
        }

        /// <summary>
        /// The kernel's name for a device, looked up afresh every time. A name
        /// that was right a moment ago may belong to another device by now: one
        /// was pulled out and another went into the same port.
        /// </summary>
        /// <summary>
        /// Phones and players that speak MTP mostly say so with the class for
        /// still image devices, which the class code covers. Older ones use a
        /// vendor specific interface instead and tell Windows what it is in a
        /// descriptor only Windows asks for. What gives them away is the shape
        /// MTP requires: subclass FF, protocol 00 and exactly three endpoints,
        /// one bulk in, one bulk out and one interrupt in. Such a device is
        /// treated as a phone. That may catch the odd device that is something
        /// else, which then needs to be trusted to work while storage is blocked.
        /// </summary>
        public static bool LooksLikeMediaTransfer(byte[] configuration)
        {
            if (configuration == null) return false;
            bool candidate = false;
            int bulkIn = 0, bulkOut = 0, interruptIn = 0, others = 0;
            int offset = 0;
            while (offset + 2 <= configuration.Length)
            {
                int length = configuration[offset];
                int type = configuration[offset + 1];
                if (length < 2 || offset + length > configuration.Length) break;
                if (type == 4 && length >= 9)
                {
                    if (candidate && bulkIn == 1 && bulkOut == 1 && interruptIn == 1 && others == 0) return true;
                    // Only the first setting of an interface says what it is when the device starts.
                    candidate = configuration[offset + 3] == 0 && configuration[offset + 5] == 0xFF &&
                                configuration[offset + 6] == 0xFF && configuration[offset + 7] == 0x00;
                    bulkIn = bulkOut = interruptIn = others = 0;
                }
                else if (type == 5 && length >= 7 && candidate)
                {
                    bool into = (configuration[offset + 2] & 0x80) != 0;
                    int transfer = configuration[offset + 3] & 0x03;
                    if (transfer == 2 && into) bulkIn++;
                    else if (transfer == 2) bulkOut++;
                    else if (transfer == 3 && into) interruptIn++;
                    else others++;
                }
                offset += length;
            }
            return candidate && bulkIn == 1 && bulkOut == 1 && interruptIn == 1 && others == 0;
        }

        private string KernelName(string instanceId)
        {
            Scan();
            lock (gate)
            {
                return kernelNames.TryGetValue(instanceId ?? "", out string name) ? name : null;
            }
        }

        public DeviceOpResult Block(string instanceId, out BlockMethod method)
        {
            method = BlockMethod.Disabled;
            return SetAuthorized(instanceId, false);
        }

        public DeviceOpResult Unblock(string instanceId)
        {
            string name = KernelName(instanceId);
            // Unplugged. The kernel keeps nothing about a device that is gone, so there is nothing to clear.
            if (name == null) return new DeviceOpResult { Ok = true, Gone = true };
            return SetAuthorized(instanceId, true);
        }

        private DeviceOpResult SetAuthorized(string instanceId, bool on)
        {
            string name = KernelName(instanceId);
            if (name == null) return DeviceOpResult.Fail("Could not find the device.");
            string file = Path.Combine(root, name, "authorized");
            try
            {
                File.WriteAllText(file, on ? "1" : "0");
                return DeviceOpResult.Success();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return DeviceOpResult.Fail("The kernel refused to switch the device " + (on ? "on" : "off") + ". " + ex.Message);
            }
        }

        // The kernel remembers nothing about unplugged devices, and one switch
        // covers a device with all its functions.

        public List<UsbDevice> ScanAbsent()
        {
            return new List<UsbDevice>();
        }

        public DeviceOpResult BlockAbsent(string instanceId)
        {
            return new DeviceOpResult { Ok = false, Gone = true, Message = "Linux keeps no record of unplugged devices." };
        }

        public List<UsbChild> Children(string instanceId)
        {
            return new List<UsbChild>();
        }

        public string ParentOf(string instanceId)
        {
            return null;
        }

        public DeviceOpResult Reinstall(string instanceId)
        {
            return SetAuthorized(instanceId, true);
        }

        // ---------- reading sysfs ----------
        //
        // A file that is not there gives null: not every device has a serial
        // number. A file that is there and cannot be read throws, and the
        // device is left out of this scan.

        private static string ReadLine(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var buffer = new byte[512];
                    int n = stream.Read(buffer, 0, buffer.Length);
                    if (n <= 0) return "";
                    string text = Encoding.UTF8.GetString(buffer, 0, n);
                    int end = text.IndexOf('\n');
                    return end >= 0 ? text.Substring(0, end) : text;
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private static byte[] ReadBytes(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int n;
                    // A device's descriptors are a few hundred bytes. Stop long before a hostile one could matter.
                    while ((n = stream.Read(buffer, 0, buffer.Length)) > 0 && memory.Length < 65536)
                    {
                        memory.Write(buffer, 0, n);
                    }
                    return memory.ToArray();
                }
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private static string ReadHex4(string path)
        {
            string text = (ReadLine(path) ?? "").Trim();
            if (text.Length != 4) return null;
            foreach (char c in text)
            {
                if (!Uri.IsHexDigit(c)) return null;
            }
            return text.ToUpperInvariant();
        }

        /// <summary>
        /// The serial number comes from the device, so it is cut down to letters,
        /// digits and a few marks before it becomes part of an ID.
        /// </summary>
        public static string CleanSerial(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return "";
            var clean = new StringBuilder();
            foreach (char c in serial.Trim())
            {
                if (clean.Length >= 64) break;
                bool plain = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                             c == '.' || c == '_' || c == '-';
                clean.Append(plain ? char.ToUpperInvariant(c) : '_');
            }
            // No "&" survives this, so a serial can never pass for an ID made up from the port ("P&1-2").
            string result = clean.ToString();
            return result.Trim('_').Length == 0 ? "" : result;
        }
    }
}
