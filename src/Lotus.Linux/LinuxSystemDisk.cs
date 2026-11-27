using System;
using System.Collections.Generic;
using System.IO;

namespace Lotus.Linux
{
    /// <summary>
    /// Finds the USB devices the running system depends on: a Linux installed on
    /// a USB disk, or a live system on a stick. Those are never blocked, and
    /// neither is a hub between them and the computer.
    /// </summary>
    public static class LinuxSystemDisk
    {
        /// <summary>Where a running system keeps the files it cannot do without, and where live systems mount their medium.</summary>
        private static readonly string[] Vital =
        {
            "/", "/boot", "/boot/efi", "/efi", "/usr", "/var", "/etc", "/home", "/root", "/opt", "/srv", "/nix",
            "/cdrom", "/run/live/medium", "/run/initramfs/live", "/lib/live/mount/medium", "/run/archiso/bootmnt"
        };

        /// <summary>
        /// The kernel names ("1-2", "1-2.3") of every USB device on the path to
        /// one of the vital filesystems. Throws when the mount table cannot be read.
        /// </summary>
        public static HashSet<string> Find(string mountInfoPath = "/proc/self/mountinfo", string sysRoot = "/sys")
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(mountInfoPath))
            {
                // 36 35 98:0 /root /mount/point rw,noatime shared:1 - ext4 /dev/sda1 rw
                string[] parts = line.Split(' ');
                if (parts.Length < 10) continue;
                string mountPoint = Unescape(parts[4]);
                if (Array.IndexOf(Vital, mountPoint) < 0) continue;
                int dash = Array.IndexOf(parts, "-");
                string source = dash >= 0 && dash + 2 < parts.Length ? Unescape(parts[dash + 2]) : "";

                string block = Path.Combine(sysRoot, "dev", "block", parts[2]);
                if (!parts[2].StartsWith("0:", StringComparison.Ordinal) && Directory.Exists(block))
                {
                    Collect(block, names, 0);
                }
                else if (source.StartsWith("/dev/", StringComparison.Ordinal))
                {
                    // Some filesystems show no device number of their own. Go by the name of the device instead.
                    string resolved = Resolve(source);
                    string byName = Path.Combine(sysRoot, "class", "block", Path.GetFileName(resolved));
                    if (Directory.Exists(byName)) Collect(byName, names, 0);
                }
            }
            return names;
        }

        /// <summary>
        /// Follows a block device to the hardware behind it. An encrypted or
        /// logical volume lists the devices it is built on as "slaves".
        /// </summary>
        private static void Collect(string blockPath, HashSet<string> names, int depth)
        {
            if (depth > 6) return;
            string real = Resolve(blockPath);
            foreach (string segment in real.Split('/'))
            {
                if (LinuxDeviceBackend.IsDeviceName(segment)) names.Add(segment);
            }
            foreach (string candidate in new[] { real, Path.GetDirectoryName(real) ?? real })
            {
                string slaves = Path.Combine(candidate, "slaves");
                if (!Directory.Exists(slaves)) continue;
                foreach (string slave in Directory.GetFileSystemEntries(slaves))
                {
                    Collect(slave, names, depth + 1);
                }
            }
        }

        private static string Resolve(string path)
        {
            try
            {
                FileSystemInfo target = Directory.Exists(path)
                    ? Directory.ResolveLinkTarget(path, true)
                    : File.ResolveLinkTarget(path, true);
                return target != null ? target.FullName : Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return path;
            }
        }

        /// <summary>The mount table writes a space as \040 and so on.</summary>
        private static string Unescape(string text)
        {
            if (text.IndexOf('\\') < 0) return text;
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 3 < text.Length && IsOctal(text[i + 1]) && IsOctal(text[i + 2]) && IsOctal(text[i + 3]))
                {
                    result.Append((char)((text[i + 1] - '0') * 64 + (text[i + 2] - '0') * 8 + (text[i + 3] - '0')));
                    i += 3;
                }
                else
                {
                    result.Append(text[i]);
                }
            }
            return result.ToString();
        }

        private static bool IsOctal(char c)
        {
            return c >= '0' && c <= '7';
        }
    }
}
