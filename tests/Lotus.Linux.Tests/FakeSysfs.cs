using System;
using System.Collections.Generic;
using System.IO;

namespace Lotus.Linux.Tests
{
    /// <summary>
    /// A made-up copy of /sys/bus/usb/devices: a folder per controller and per
    /// device, with the files LOTUS reads and writes. A device that is plugged
    /// in starts authorized or not, depending on its controller's
    /// "authorized_default", the way the kernel does it.
    /// </summary>
    internal sealed class FakeSysfs : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "lotus-sys-" + Guid.NewGuid().ToString("N"));

        public static readonly byte[] Storage = { 0x08, 0x06, 0x50 };
        public static readonly byte[] Keyboard = { 0x03, 0x01, 0x01 };
        public static readonly byte[] Mouse = { 0x03, 0x01, 0x02 };
        public static readonly byte[] Printer = { 0x07, 0x01, 0x02 };
        public static readonly byte[] Camera = { 0x0E, 0x01, 0x00 };
        public static readonly byte[] Audio = { 0x01, 0x01, 0x00 };
        public static readonly byte[] Hub = { 0x09, 0x00, 0x00 };

        public FakeSysfs(int controllers = 2)
        {
            Directory.CreateDirectory(Root);
            for (int i = 1; i <= controllers; i++) Controller("usb" + i);
        }

        public void Controller(string name, string authorizedDefault = "1")
        {
            string dir = Path.Combine(Root, name);
            Directory.CreateDirectory(dir);
            Put(dir, "authorized_default", authorizedDefault);
            Put(dir, "idVendor", "1d6b");
            Put(dir, "idProduct", "0002");
            Put(dir, "authorized", "1");
        }

        public string Default(string controller)
        {
            return File.ReadAllText(Path.Combine(Root, controller, "authorized_default")).Trim();
        }

        public void SetDefault(string controller, string value)
        {
            Put(Path.Combine(Root, controller), "authorized_default", value);
        }

        /// <summary>One configuration with the given interfaces, each three bytes: class, subclass, protocol.</summary>
        public static byte[] Configuration(params byte[][] interfaces)
        {
            var body = new List<byte>();
            for (int i = 0; i < interfaces.Length; i++)
            {
                body.AddRange(new byte[] { 9, 4, (byte)i, 0, 0, interfaces[i][0], interfaces[i][1], interfaces[i][2], 0 });
            }
            int total = 9 + body.Count;
            var all = new List<byte> { 9, 2, (byte)(total & 0xFF), (byte)(total >> 8), (byte)interfaces.Length, 1, 0, 0x80, 50 };
            all.AddRange(body);
            return all.ToArray();
        }

        public static byte[] Descriptors(int vid, int pid, byte deviceClass, params byte[][] configurations)
        {
            var all = new List<byte>
            {
                18, 1, 0x00, 0x02, deviceClass, 0, 0, 64,
                (byte)(vid & 0xFF), (byte)(vid >> 8), (byte)(pid & 0xFF), (byte)(pid >> 8),
                0x00, 0x01, 1, 2, 3, (byte)configurations.Length
            };
            foreach (byte[] configuration in configurations) all.AddRange(configuration);
            return all.ToArray();
        }

        /// <summary>Plugs a device in. The name is the kernel's: bus, dash, the chain of ports.</summary>
        public void Plug(string name, int vid, int pid, string product, string serial, byte[][] interfaces,
            byte deviceClass = 0, string maker = null, string removable = "removable", string authorized = null,
            byte[] descriptors = null, bool withDescriptors = true)
        {
            string dir = Path.Combine(Root, name);
            Directory.CreateDirectory(dir);
            Put(dir, "idVendor", vid.ToString("x4"));
            Put(dir, "idProduct", pid.ToString("x4"));
            if (product != null) Put(dir, "product", product);
            if (maker != null) Put(dir, "manufacturer", maker);
            if (serial != null) Put(dir, "serial", serial);
            Put(dir, "bDeviceClass", deviceClass.ToString("x2"));
            Put(dir, "removable", removable);
            string bus = "usb" + name.Substring(0, name.IndexOf('-'));
            Put(dir, "authorized", authorized ?? (Default(bus) == "0" ? "0" : "1"));
            if (withDescriptors)
            {
                File.WriteAllBytes(Path.Combine(dir, "descriptors"),
                    descriptors ?? Descriptors(vid, pid, deviceClass, Configuration(interfaces)));
            }
            // What the kernel adds for a running device. LOTUS must not take these for devices.
            // (Windows, where these tests also run, has no colons in file names.)
            if (!OperatingSystem.IsWindows()) Directory.CreateDirectory(Path.Combine(Root, name + ":1.0"));
        }

        public void PlugStick(string name = "1-2", string serial = "4C530001230505116353")
        {
            Plug(name, 0x0781, 0x5567, "Cruzer Blade", serial, new[] { Storage }, maker: "SanDisk");
        }

        public void PlugKeyboard(string name = "1-1")
        {
            Plug(name, 0x046D, 0xC31C, "USB Keyboard", null, new[] { Keyboard }, maker: "Logitech");
        }

        public void PlugPrinter(string name = "2-2")
        {
            Plug(name, 0x03F0, 0x2B17, "LaserJet 1020", "JL1234", new[] { Printer }, maker: "Hewlett-Packard");
        }

        public void Unplug(string name)
        {
            Directory.Delete(Path.Combine(Root, name), true);
            string function = Path.Combine(Root, name + ":1.0");
            if (Directory.Exists(function)) Directory.Delete(function, true);
        }

        public string Authorized(string name)
        {
            return File.ReadAllText(Path.Combine(Root, name, "authorized")).Trim();
        }

        public void SetAuthorized(string name, string value)
        {
            Put(Path.Combine(Root, name), "authorized", value);
        }

        private static void Put(string dir, string file, string text)
        {
            File.WriteAllText(Path.Combine(dir, file), text + "\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}
