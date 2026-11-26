using System;
using Lotus.Core.Native;

namespace Lotus.Core
{
    /// <summary>
    /// Asks Windows to call back whenever a device appears, starts or goes away.
    /// The callback arrives before a new device has finished installing, which lets
    /// the service block it early.
    /// </summary>
    public sealed class DeviceWatcher : IDisposable
    {
        /// <summary>Windows found the device on the bus. Its driver has not been started yet.</summary>
        public const int Enumerated = 7;
        public const int Started = 8;
        public const int Removed = 9;

        private readonly Action<int, string> onChange;
        private CM_NOTIFY_CALLBACK callback; // kept in a field so the garbage collector leaves it alone
        private IntPtr handle = IntPtr.Zero;

        /// <param name="onChange">Gets the kind of change and the instance ID of the device it is about.</param>
        public DeviceWatcher(Action<int, string> onChange)
        {
            this.onChange = onChange ?? throw new ArgumentNullException(nameof(onChange));
        }

        /// <summary>Returns false when notifications are not available. The caller then relies on polling.</summary>
        public bool Start()
        {
            if (handle != IntPtr.Zero) return true;
            try
            {
                // CM_NOTIFY_FILTER: size, flags, filter type, reserved, then a 400 byte union.
                var filter = new byte[CfgMgr.CM_NOTIFY_FILTER_SIZE];
                BitConverter.GetBytes(CfgMgr.CM_NOTIFY_FILTER_SIZE).CopyTo(filter, 0);
                BitConverter.GetBytes(CfgMgr.CM_NOTIFY_FILTER_FLAG_ALL_DEVICE_INSTANCES).CopyTo(filter, 4);
                BitConverter.GetBytes(CfgMgr.CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE).CopyTo(filter, 8);

                callback = OnNotify;
                int cr = CfgMgr.CM_Register_Notification(filter, IntPtr.Zero, callback, out handle);
                if (cr != CfgMgr.CR_SUCCESS)
                {
                    handle = IntPtr.Zero;
                    callback = null;
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException)
            {
                handle = IntPtr.Zero;
                callback = null;
                return false;
            }
        }

        private int OnNotify(IntPtr notify, IntPtr context, int action, IntPtr eventData, int eventDataSize)
        {
            try
            {
                onChange(action, ReadInstanceId(eventData, eventDataSize));
            }
            catch
            {
                // An exception must not travel back into Windows.
            }
            return 0; // ERROR_SUCCESS
        }

        /// <summary>
        /// The event data is a small header followed by the instance ID as text.
        /// Returns null when the event carries no usable ID.
        /// </summary>
        private static string ReadInstanceId(IntPtr eventData, int eventDataSize)
        {
            const int header = 8;
            if (eventData == IntPtr.Zero || eventDataSize <= header + 2) return null;
            int chars = Math.Min((eventDataSize - header) / 2, CfgMgr.MaxDeviceIdLength + 1);
            string text = System.Runtime.InteropServices.Marshal.PtrToStringUni(IntPtr.Add(eventData, header), chars);
            int end = text.IndexOf('\0');
            return end < 0 ? text : text.Substring(0, end);
        }

        public void Dispose()
        {
            IntPtr h = handle;
            handle = IntPtr.Zero;
            if (h != IntPtr.Zero) CfgMgr.CM_Unregister_Notification(h);
            callback = null;
        }
    }
}
