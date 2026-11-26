using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Lotus.Core.Native
{
    internal static class Kernel
    {
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint GENERIC_WRITE = 0x40000000;

        public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
        public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr inBuffer,
            int inBufferSize, [Out] byte[] outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);

        /// <summary>For requests that send a filled buffer and get the answer back in the same one.</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, [In, Out] byte[] inBuffer,
            int inBufferSize, [In, Out] byte[] outBuffer, int outBufferSize, out int bytesReturned,
            IntPtr overlapped);
    }
}
