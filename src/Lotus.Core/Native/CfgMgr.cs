using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Lotus.Core.Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;

        public DEVPROPKEY(string guid, uint pid)
        {
            fmtid = new Guid(guid);
            this.pid = pid;
        }
    }

    internal delegate int CM_NOTIFY_CALLBACK(IntPtr hNotify, IntPtr context, int action, IntPtr eventData,
        int eventDataSize);

    /// <summary>
    /// The Windows Configuration Manager API (cfgmgr32.dll). It is the same API
    /// Device Manager uses to list, disable and enable devices.
    /// </summary>
    internal static class CfgMgr
    {
        private const string Dll = "cfgmgr32.dll";

        // Return codes
        public const int CR_SUCCESS = 0x00;
        public const int CR_INVALID_FLAG = 0x04;
        public const int CR_INVALID_DEVNODE = 0x05;
        public const int CR_NO_SUCH_DEVNODE = 0x0D;
        public const int CR_FAILURE = 0x13;
        public const int CR_REMOVE_VETOED = 0x17;
        public const int CR_BUFFER_SMALL = 0x1A;
        public const int CR_INVALID_DEVICE_ID = 0x1E;
        public const int CR_NEED_RESTART = 0x22;
        public const int CR_DEVICE_NOT_THERE = 0x24;
        public const int CR_NO_SUCH_VALUE = 0x25;
        public const int CR_NOT_DISABLEABLE = 0x28;
        public const int CR_ACCESS_DENIED = 0x33;

        // CM_Locate_DevNode
        public const int CM_LOCATE_DEVNODE_NORMAL = 0;
        public const int CM_LOCATE_DEVNODE_PHANTOM = 1;

        // CM_Get_Device_ID_List
        public const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
        public const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
        public const int CM_GETIDLIST_FILTER_CLASS = 0x200;

        // CM_Get_Device_Interface_List
        public const int CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;

        // Registry properties
        public const int CM_DRP_DEVICEDESC = 0x01;
        public const int CM_DRP_HARDWAREID = 0x02;
        public const int CM_DRP_COMPATIBLEIDS = 0x03;
        public const int CM_DRP_SERVICE = 0x05;
        public const int CM_DRP_CLASS = 0x08;
        public const int CM_DRP_CONFIGFLAGS = 0x0B;
        public const int CM_DRP_MFG = 0x0C;
        public const int CM_DRP_FRIENDLYNAME = 0x0D;
        public const int CM_DRP_LOCATION_INFORMATION = 0x0E;
        public const int CM_DRP_CAPABILITIES = 0x10;
        public const int CM_DRP_ADDRESS = 0x1D;

        public const int CONFIGFLAG_DISABLED = 0x1;
        public const int CM_DEVCAP_UNIQUEID = 0x10;
        public const int CM_DEVCAP_REMOVABLE = 0x4;

        // Status flags
        public const int DN_STARTED = 0x8;
        public const int DN_HAS_PROBLEM = 0x400;
        public const int DN_DISABLEABLE = 0x2000;

        public const int CM_PROB_WILL_BE_REMOVED = 0x15;
        public const int CM_PROB_DISABLED = 0x16;

        // CM_Disable_DevNode
        public const int CM_DISABLE_UI_NOT_OK = 0x4;
        public const int CM_DISABLE_PERSIST = 0x8;

        // CM_Query_And_Remove_SubTree
        public const int CM_REMOVE_UI_NOT_OK = 0x1;
        public const int CM_REMOVE_NO_RESTART = 0x2;

        // CM_Setup_DevNode
        public const int CM_SETUP_DEVNODE_READY = 0x0;
        public const int CM_SETUP_DEVNODE_RESET = 0x4;

        // CM_Reenumerate_DevNode
        public const int CM_REENUMERATE_SYNCHRONOUS = 0x1;
        public const int CM_REENUMERATE_RETRY_INSTALLATION = 0x2;

        // Notifications
        public const int CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES = 0x2;
        public const int CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;
        public const int CM_NOTIFY_FILTER_SIZE = 416;

        public const int REG_SZ = 1;
        public const int REG_MULTI_SZ = 7;
        public const int REG_DWORD = 4;

        public const int DEVPROP_TYPE_BOOLEAN = 0x11;
        public const int DEVPROP_TYPE_STRING = 0x12;

        public static readonly DEVPROPKEY DEVPKEY_Device_BusReportedDeviceDesc =
            new DEVPROPKEY("540b947e-8b40-45bc-a8a2-6a0b894cbda2", 4);
        public static readonly DEVPROPKEY DEVPKEY_Device_InLocalMachineContainer =
            new DEVPROPKEY("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 4);
        public static readonly DEVPROPKEY DEVPKEY_Device_DriverProvider =
            new DEVPROPKEY("a8b865dd-2e3d-4094-ad97-e593a70c75d6", 9);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_List_SizeW(out int length, string filter, int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, int bufferLength,
            int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);

        [DllImport(Dll)]
        public static extern int CM_Get_Parent(out int parent, int devInst, int flags);

        [DllImport(Dll)]
        public static extern int CM_Get_Child(out int child, int devInst, int flags);

        [DllImport(Dll)]
        public static extern int CM_Get_Sibling(out int sibling, int devInst, int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_IDW(int devInst, [Out] char[] buffer, int bufferLength, int flags);

        [DllImport(Dll)]
        public static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Get_DevNode_Registry_PropertyW(int devInst, int property, out int regType,
            [Out] byte[] buffer, ref int length, int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Set_DevNode_Registry_PropertyW(int devInst, int property, byte[] buffer,
            int length, int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Get_DevNode_PropertyW(int devInst, ref DEVPROPKEY key, out int propertyType,
            [Out] byte[] buffer, ref int bufferSize, int flags);

        [DllImport(Dll)]
        public static extern int CM_Disable_DevNode(int devInst, int flags);

        [DllImport(Dll)]
        public static extern int CM_Enable_DevNode(int devInst, int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Query_And_Remove_SubTreeW(int devInst, out int vetoType, StringBuilder vetoName,
            int vetoNameLength, int flags);

        [DllImport(Dll)]
        public static extern int CM_Setup_DevNode(int devInst, int flags);

        [DllImport(Dll)]
        public static extern int CM_Reenumerate_DevNode(int devInst, int flags);

        [DllImport(Dll)]
        public static extern int CM_Register_Notification(byte[] filter, IntPtr context,
            CM_NOTIFY_CALLBACK callback, out IntPtr notifyContext);

        [DllImport(Dll)]
        public static extern int CM_Unregister_Notification(IntPtr notifyContext);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid interfaceClass,
            string deviceId, int flags);

        [DllImport(Dll, CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string deviceId,
            [Out] char[] buffer, int bufferLength, int flags);

        // ---------- helpers ----------

        public const int MaxDeviceIdLength = 200;

        /// <summary>Instance IDs of present devices that match a filter.</summary>
        public static List<string> GetDeviceIds(string filter, int flags)
        {
            var result = new List<string>();
            // The list can grow between the size call and the list call, so retry a few times.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int cr = CM_Get_Device_ID_List_SizeW(out int length, filter, flags);
                if (cr != CR_SUCCESS || length <= 1) return result;
                var buffer = new char[length + 64];
                cr = CM_Get_Device_ID_ListW(filter, buffer, buffer.Length, flags);
                if (cr == CR_BUFFER_SMALL) continue;
                if (cr != CR_SUCCESS) return result;
                SplitMultiString(buffer, buffer.Length, result);
                return result;
            }
            return result;
        }

        public static List<string> GetInterfacePaths(Guid interfaceClass, string deviceId)
        {
            var result = new List<string>();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int cr = CM_Get_Device_Interface_List_SizeW(out int length, ref interfaceClass, deviceId,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
                if (cr != CR_SUCCESS || length <= 1) return result;
                var buffer = new char[length + 64];
                cr = CM_Get_Device_Interface_ListW(ref interfaceClass, deviceId, buffer, buffer.Length,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT);
                if (cr == CR_BUFFER_SMALL) continue;
                if (cr != CR_SUCCESS) return result;
                SplitMultiString(buffer, buffer.Length, result);
                return result;
            }
            return result;
        }

        private static void SplitMultiString(char[] buffer, int length, List<string> into)
        {
            int start = 0;
            for (int i = 0; i < length; i++)
            {
                if (buffer[i] != '\0') continue;
                if (i == start) break; // an empty string ends the list
                into.Add(new string(buffer, start, i - start));
                start = i + 1;
            }
        }

        public static string GetDeviceId(int devInst)
        {
            var buffer = new char[MaxDeviceIdLength + 1];
            int cr = CM_Get_Device_IDW(devInst, buffer, buffer.Length, 0);
            if (cr != CR_SUCCESS) return null;
            int end = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, end < 0 ? buffer.Length : end);
        }

        private static byte[] GetRegistryProperty(int devInst, int property, out int regType)
        {
            int length = 1024;
            var buffer = new byte[length];
            int cr = CM_Get_DevNode_Registry_PropertyW(devInst, property, out regType, buffer, ref length, 0);
            if (cr == CR_BUFFER_SMALL)
            {
                buffer = new byte[length];
                cr = CM_Get_DevNode_Registry_PropertyW(devInst, property, out regType, buffer, ref length, 0);
            }
            if (cr != CR_SUCCESS) return null;
            if (length < buffer.Length) Array.Resize(ref buffer, length);
            return buffer;
        }

        public static string GetString(int devInst, int property)
        {
            byte[] data = GetRegistryProperty(devInst, property, out int _);
            if (data == null) return "";
            string text = Encoding.Unicode.GetString(data);
            int end = text.IndexOf('\0');
            return (end < 0 ? text : text.Substring(0, end)).Trim();
        }

        public static List<string> GetMultiString(int devInst, int property)
        {
            var result = new List<string>();
            byte[] data = GetRegistryProperty(devInst, property, out int _);
            if (data == null) return result;
            string text = Encoding.Unicode.GetString(data);
            foreach (string part in text.Split('\0'))
            {
                if (part.Length > 0) result.Add(part);
            }
            return result;
        }

        public static bool TryGetInt(int devInst, int property, out int value)
        {
            value = 0;
            byte[] data = GetRegistryProperty(devInst, property, out int _);
            if (data == null || data.Length < 4) return false;
            value = BitConverter.ToInt32(data, 0);
            return true;
        }

        public static string GetDevPropString(int devInst, DEVPROPKEY key)
        {
            int size = 1024;
            var buffer = new byte[size];
            int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out int type, buffer, ref size, 0);
            if (cr == CR_BUFFER_SMALL)
            {
                buffer = new byte[size];
                cr = CM_Get_DevNode_PropertyW(devInst, ref key, out type, buffer, ref size, 0);
            }
            if (cr != CR_SUCCESS || type != DEVPROP_TYPE_STRING || size < 2) return "";
            string text = Encoding.Unicode.GetString(buffer, 0, size);
            int end = text.IndexOf('\0');
            return (end < 0 ? text : text.Substring(0, end)).Trim();
        }

        /// <summary>Reads a boolean device property. Returns null when the device does not have it.</summary>
        public static bool? GetDevPropBool(int devInst, DEVPROPKEY key)
        {
            int size = 16;
            var buffer = new byte[size];
            int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out int type, buffer, ref size, 0);
            if (cr != CR_SUCCESS || type != DEVPROP_TYPE_BOOLEAN || size < 1) return null;
            return buffer[0] != 0;
        }

        public static string Describe(int cr)
        {
            switch (cr)
            {
                case CR_SUCCESS: return "ok";
                case CR_INVALID_FLAG: return "Windows did not accept the request flags";
                case CR_INVALID_DEVNODE: return "the device handle is not valid";
                case CR_NO_SUCH_DEVNODE: return "Windows no longer knows this device";
                case CR_FAILURE: return "Windows reported a general failure";
                case CR_REMOVE_VETOED: return "a program is still using the device";
                case CR_INVALID_DEVICE_ID: return "the device ID is not valid";
                case CR_NEED_RESTART: return "Windows needs a restart to finish";
                case CR_DEVICE_NOT_THERE: return "the device is not connected";
                case CR_NOT_DISABLEABLE: return "Windows does not allow this device to be disabled";
                case CR_ACCESS_DENIED: return "access was denied (administrator rights are required)";
                default: return "Configuration Manager error 0x" + cr.ToString("X2");
            }
        }
    }
}
