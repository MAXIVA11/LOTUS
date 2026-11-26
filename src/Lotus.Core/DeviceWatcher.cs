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
        private readonly Action onChange;
        private CM_NOTIFY_CALLBACK callback; // kept in a field so the garbage collector leaves it alone
        private IntPtr handle = IntPtr.Zero;

        public DeviceWatcher(Action onChange)
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
                onChange();
            }
            catch
            {
                // An exception must not travel back into Windows.
            }
            return 0; // ERROR_SUCCESS
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
