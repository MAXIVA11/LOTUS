using System;
using System.Collections.Generic;
using System.IO;
using Lotus.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace Lotus.Core
{
    /// <summary>
    /// Finds the devices Windows itself is running from. If Windows boots from a
    /// USB disk, disabling that disk would take the whole system down, so LOTUS
    /// looks this up before it blocks any storage.
    /// </summary>
    public static class SystemDisk
    {
        private static readonly Guid DiskInterface = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
        private const string DiskClassGuid = "{4d36e967-e325-11ce-bfc1-08002be10318}";

        /// <summary>
        /// Instance IDs (upper case) of the system disk and of every device above it
        /// in the device tree. Throws when the lookup fails, so that the caller can
        /// hold back instead of guessing.
        /// </summary>
        public static HashSet<string> FindAncestors()
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            {
                throw new InvalidOperationException("Could not work out which drive Windows is on.");
            }

            HashSet<int> diskNumbers = GetDiskNumbers(@"\\.\" + root.Substring(0, 2));
            if (diskNumbers.Count == 0)
            {
                throw new InvalidOperationException("Windows did not report which disk holds drive " + root + ".");
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int matched = 0;
            List<string> disks = CfgMgr.GetDeviceIds(DiskClassGuid,
                CfgMgr.CM_GETIDLIST_FILTER_CLASS | CfgMgr.CM_GETIDLIST_FILTER_PRESENT);
            foreach (string diskId in disks)
            {
                foreach (string path in CfgMgr.GetInterfacePaths(DiskInterface, diskId))
                {
                    if (!TryGetDeviceNumber(path, out int number) || !diskNumbers.Contains(number)) continue;
                    matched++;
                    AddWithAncestors(diskId, result);
                }
            }

            if (matched == 0)
            {
                throw new InvalidOperationException("Could not find the disk device behind drive " + root + ".");
            }
            return result;
        }

        private static void AddWithAncestors(string instanceId, HashSet<string> into)
        {
            if (CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL) !=
                CfgMgr.CR_SUCCESS)
            {
                into.Add(instanceId.ToUpperInvariant());
                return;
            }
            // The device tree is shallow. The limit only guards against a loop.
            for (int depth = 0; depth < 64; depth++)
            {
                string id = CfgMgr.GetDeviceId(node);
                if (id != null) into.Add(id.ToUpperInvariant());
                if (CfgMgr.CM_Get_Parent(out int parent, node, 0) != CfgMgr.CR_SUCCESS) break;
                node = parent;
            }
        }

        private static HashSet<int> GetDiskNumbers(string volumePath)
        {
            var numbers = new HashSet<int>();
            using (SafeFileHandle handle = Kernel.CreateFileW(volumePath, 0,
                       Kernel.FILE_SHARE_READ | Kernel.FILE_SHARE_WRITE, IntPtr.Zero, Kernel.OPEN_EXISTING, 0,
                       IntPtr.Zero))
            {
                if (handle.IsInvalid) return numbers;
                // VOLUME_DISK_EXTENTS: a 4 byte count, 4 bytes of padding, then 24 bytes per extent.
                var buffer = new byte[8 + 24 * 64];
                if (!Kernel.DeviceIoControl(handle, Kernel.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0,
                        buffer, buffer.Length, out int returned, IntPtr.Zero) || returned < 8)
                {
                    return numbers;
                }
                int count = BitConverter.ToInt32(buffer, 0);
                for (int i = 0; i < count && 8 + i * 24 + 4 <= returned; i++)
                {
                    numbers.Add(BitConverter.ToInt32(buffer, 8 + i * 24));
                }
            }
            return numbers;
        }

        private static bool TryGetDeviceNumber(string interfacePath, out int number)
        {
            number = -1;
            using (SafeFileHandle handle = Kernel.CreateFileW(interfacePath, 0,
                       Kernel.FILE_SHARE_READ | Kernel.FILE_SHARE_WRITE, IntPtr.Zero, Kernel.OPEN_EXISTING, 0,
                       IntPtr.Zero))
            {
                if (handle.IsInvalid) return false;
                // STORAGE_DEVICE_NUMBER: device type, device number, partition number.
                var buffer = new byte[12];
                if (!Kernel.DeviceIoControl(handle, Kernel.IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buffer,
                        buffer.Length, out int returned, IntPtr.Zero) || returned < 8)
                {
                    return false;
                }
                number = BitConverter.ToInt32(buffer, 4);
                return true;
            }
        }
    }
}
