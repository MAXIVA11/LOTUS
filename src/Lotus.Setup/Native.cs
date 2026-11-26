using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Lotus.Setup
{
    /// <summary>Creates, updates and deletes the Windows service.</summary>
    internal static class ServiceControl
    {
        private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x10;
        private const uint SERVICE_AUTO_START = 0x2;
        private const uint SERVICE_ERROR_NORMAL = 0x1;
        private const uint SERVICE_CONFIG_DESCRIPTION = 1;
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
        private const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_DESCRIPTION
        {
            public string lpDescription;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateServiceW(IntPtr manager, string name, string displayName, uint access,
            uint serviceType, uint startType, uint errorControl, string binaryPath, string loadOrderGroup,
            IntPtr tagId, string dependencies, string account, string password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType,
            uint errorControl, string binaryPath, string loadOrderGroup, IntPtr tagId, string dependencies,
            string account, string password, string displayName);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref SERVICE_DESCRIPTION info);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        public static bool Exists(string name)
        {
            IntPtr manager = OpenManager();
            try
            {
                IntPtr service = OpenServiceW(manager, name, SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero) return false;
                CloseServiceHandle(service);
                return true;
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        /// <summary>
        /// Creates the service, or points an existing one at the new program path.
        /// The path is stored in quotes, so a space in "Program Files" cannot be
        /// misread as the end of the program name.
        /// </summary>
        public static void CreateOrUpdate(string name, string displayName, string description, string exePath)
        {
            string quoted = "\"" + exePath + "\"";
            IntPtr manager = OpenManager();
            try
            {
                IntPtr service = OpenServiceW(manager, name, SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero)
                {
                    service = CreateServiceW(manager, name, displayName, SERVICE_ALL_ACCESS,
                        SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, quoted, null,
                        IntPtr.Zero, null, null, null);
                    if (service == IntPtr.Zero) throw LastError("create the service");
                }
                else if (!ChangeServiceConfigW(service, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                             SERVICE_ERROR_NORMAL, quoted, null, IntPtr.Zero, null, null, null, displayName))
                {
                    Win32Exception error = LastError("update the service");
                    CloseServiceHandle(service);
                    throw error;
                }

                try
                {
                    var info = new SERVICE_DESCRIPTION { lpDescription = description };
                    ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, ref info);
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        /// <summary>Deletes the service. Returns false when it did not exist.</summary>
        public static bool Delete(string name)
        {
            IntPtr manager = OpenManager();
            try
            {
                IntPtr service = OpenServiceW(manager, name, SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    if (code == ERROR_SERVICE_DOES_NOT_EXIST) return false;
                    throw new Win32Exception(code, "Could not open the service: " + new Win32Exception(code).Message);
                }
                try
                {
                    if (!DeleteService(service))
                    {
                        int code = Marshal.GetLastWin32Error();
                        if (code != ERROR_SERVICE_MARKED_FOR_DELETE)
                        {
                            throw new Win32Exception(code,
                                "Could not delete the service: " + new Win32Exception(code).Message);
                        }
                    }
                    return true;
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        private static IntPtr OpenManager()
        {
            IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
            if (manager == IntPtr.Zero) throw LastError("open the service manager");
            return manager;
        }

        private static Win32Exception LastError(string what)
        {
            int code = Marshal.GetLastWin32Error();
            return new Win32Exception(code, "Could not " + what + ": " + new Win32Exception(code).Message);
        }
    }

    /// <summary>Writes .lnk shortcut files through the Windows shell.</summary>
    internal static class Shortcut
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
         Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, int flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int max, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
            void Resolve(IntPtr window, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        public static void Create(string linkPath, string target, string arguments, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetArguments(arguments ?? "");
                link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target));
                link.SetDescription(description ?? "");
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(linkPath, false);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }

}
