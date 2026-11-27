using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Lotus.Linux
{
    /// <summary>
    /// Finds the keyboards and mice the kernel has that LOTUS does not manage:
    /// the keyboard and touchpad of a laptop, a keyboard on a PS/2 port, one that
    /// is connected over Bluetooth. LOTUS asks this before it concludes that the
    /// computer has no working keyboard or mouse left.
    /// </summary>
    public static class LinuxInput
    {
        /// <summary>Where the kernel lists every input device with its bus, its handlers and its keys.</summary>
        public const string DevicesFile = "/proc/bus/input/devices";

        private const int BusUsb = 0x03;
        private const int BusBluetooth = 0x05;
        private const int BusVirtual = 0x06;
        private const int KeyA = 30;

        /// <summary>The names of the working keyboards and mice that are not USB devices.</summary>
        public static List<string> FindBesidesUsb(string path = DevicesFile)
        {
            try
            {
                return path != null && File.Exists(path) ? Parse(File.ReadAllText(path)) : new List<string>();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Reads the kernel's list. Every device is a block of lines, each with a
        /// letter in front: I for the bus, N for the name, S for the place in
        /// sysfs, H for the handlers and B for what the device can report.
        ///
        /// A keyboard is something the "kbd" handler listens to that has the letter
        /// keys. That leaves out the power button and the lid switch, which the
        /// kernel also treats as keyboards. A mouse is anything with a mouse
        /// handler, which covers touchpads. USB devices do not count, because LOTUS
        /// judges them with the other USB devices, and neither do devices that
        /// exist in software only.
        /// </summary>
        public static List<string> Parse(string text)
        {
            var found = new List<string>();
            int bus = -1;
            string name = "", sysfs = "", handlers = "", keys = "";
            Action close = () =>
            {
                // A Bluetooth keyboard comes in through a part of the kernel whose path
                // says "virtual" as well. Its bus is what tells it from a made-up device.
                bool madeUp = bus == BusVirtual ||
                              (bus != BusBluetooth && sysfs.IndexOf("/virtual/", StringComparison.Ordinal) >= 0);
                if (bus >= 0 && bus != BusUsb && !madeUp && sysfs.IndexOf("/usb", StringComparison.Ordinal) < 0)
                {
                    bool keyboard = HasWord(handlers, "kbd") && HasKey(keys, KeyA);
                    bool mouse = handlers.IndexOf("mouse", StringComparison.Ordinal) >= 0;
                    if (keyboard || mouse) found.Add(name.Length > 0 ? name : "input device");
                }
                bus = -1;
                name = sysfs = handlers = keys = "";
            };

            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                {
                    close();
                    continue;
                }
                if (line.StartsWith("I:", StringComparison.Ordinal))
                {
                    int at = line.IndexOf("Bus=", StringComparison.Ordinal);
                    if (at >= 0 && at + 8 <= line.Length &&
                        int.TryParse(line.Substring(at + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
                    {
                        bus = value;
                    }
                }
                else if (line.StartsWith("N: Name=", StringComparison.Ordinal))
                {
                    name = line.Substring(8).Trim().Trim('"');
                }
                else if (line.StartsWith("S: Sysfs=", StringComparison.Ordinal))
                {
                    sysfs = line.Substring(9).Trim();
                }
                else if (line.StartsWith("H: Handlers=", StringComparison.Ordinal))
                {
                    handlers = line.Substring(12).Trim();
                }
                else if (line.StartsWith("B: KEY=", StringComparison.Ordinal))
                {
                    keys = line.Substring(7).Trim();
                }
            }
            close();
            return found;
        }

        private static bool HasWord(string list, string word)
        {
            foreach (string part in list.Split(' '))
            {
                if (part == word) return true;
            }
            return false;
        }

        /// <summary>The kernel prints the keys as hexadecimal words of 64 bits, the highest first.</summary>
        private static bool HasKey(string bitmap, int key)
        {
            string[] words = bitmap.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int index = words.Length - 1 - key / 64;
            if (index < 0 || index >= words.Length) return false;
            return ulong.TryParse(words[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong word) &&
                   (word >> (key % 64) & 1) == 1;
        }
    }
}
