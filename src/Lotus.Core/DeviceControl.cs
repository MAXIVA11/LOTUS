using System;
using System.Runtime.InteropServices;
using Lotus.Core.Native;

namespace Lotus.Core
{
    public sealed class DeviceOpResult
    {
        public bool Ok;
        /// <summary>Windows has no record of the device any more, so there is nothing left to do.</summary>
        public bool Gone;
        /// <summary>Windows does not allow this device to be disabled. Keyboards are the usual case.</summary>
        public bool NotDisableable;
        /// <summary>A program had the device open, so Windows refused to stop it for now.</summary>
        public bool Vetoed;
        public string Message = "";

        public static DeviceOpResult Success()
        {
            return new DeviceOpResult { Ok = true };
        }

        public static DeviceOpResult Fail(string message)
        {
            return new DeviceOpResult { Ok = false, Message = message };
        }
    }

    /// <summary>How LOTUS switched a device off.</summary>
    public enum BlockMethod
    {
        /// <summary>Disabled like in Device Manager. The device stays off across replugging and restarts.</summary>
        Disabled,
        /// <summary>
        /// Removed from the running system, and Windows would not take a lasting
        /// disable on top. Windows forgets the removal when the device is plugged
        /// in again, so the service removes it again each time.
        /// </summary>
        Removed
    }

    /// <summary>
    /// Disables and enables devices the way Device Manager does. Both calls need
    /// administrator rights.
    /// </summary>
    public static class DeviceControl
    {
        /// <summary>
        /// Disables a connected device and makes the disabled state stick, so the
        /// device stays off when it is unplugged and plugged back in, and after a restart.
        /// </summary>
        public static DeviceOpResult Disable(string instanceId)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not find the device: " + CfgMgr.Describe(cr) + ".");
            }

            cr = CfgMgr.CM_Disable_DevNode(node, CfgMgr.CM_DISABLE_UI_NOT_OK | CfgMgr.CM_DISABLE_PERSIST);
            if (cr == CfgMgr.CR_INVALID_FLAG)
            {
                // Older Windows versions do not know the persist flag and always persist.
                cr = CfgMgr.CM_Disable_DevNode(node, CfgMgr.CM_DISABLE_UI_NOT_OK);
            }
            if (cr != CfgMgr.CR_SUCCESS)
            {
                DeviceOpResult failed =
                    DeviceOpResult.Fail("Windows refused to disable the device: " + CfgMgr.Describe(cr) + ".");
                failed.NotDisableable = cr == CfgMgr.CR_NOT_DISABLEABLE;
                failed.Vetoed = cr == CfgMgr.CR_REMOVE_VETOED;
                // A refused request must not leave the "disabled" mark behind.
                ClearDisabledFlag(node);
                return failed;
            }

            // Success from the call is not enough. Check that the device really stopped.
            if (CfgMgr.CM_Get_DevNode_Status(out int status, out int problem, node, 0) == CfgMgr.CR_SUCCESS)
            {
                bool disabled = (status & CfgMgr.DN_HAS_PROBLEM) != 0 && problem == CfgMgr.CM_PROB_DISABLED;
                if (!disabled && (status & CfgMgr.DN_STARTED) != 0)
                {
                    DeviceOpResult running = DeviceOpResult.Fail(
                        "The device is still running. Windows will turn it off the next time it is connected.");
                    running.Vetoed = true;
                    return running;
                }
            }
            return DeviceOpResult.Success();
        }

        /// <summary>
        /// Enables a device LOTUS disabled earlier. Works for a connected device and
        /// for one that has been unplugged since, so nothing is left switched off.
        /// </summary>
        public static DeviceOpResult Enable(string instanceId)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            if (cr == CfgMgr.CR_SUCCESS)
            {
                cr = CfgMgr.CM_Enable_DevNode(node, 0);
                if (cr != CfgMgr.CR_SUCCESS)
                {
                    return DeviceOpResult.Fail("Windows refused to enable the device: " + CfgMgr.Describe(cr) + ".");
                }
                if (CfgMgr.CM_Get_DevNode_Status(out int status, out int problem, node, 0) == CfgMgr.CR_SUCCESS &&
                    (status & CfgMgr.DN_HAS_PROBLEM) != 0 && problem == CfgMgr.CM_PROB_DISABLED)
                {
                    return DeviceOpResult.Fail("The device is still disabled after the enable request.");
                }
                // A disable request that was refused while the device was busy can
                // still leave the "disabled" mark behind. Make sure it is gone.
                return ClearDisabledFlag(node);
            }

            // Not connected. Find the record Windows keeps for it and clear the disabled flag.
            cr = CfgMgr.CM_Locate_DevNodeW(out node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_PHANTOM);
            if (cr == CfgMgr.CR_NO_SUCH_DEVNODE || cr == CfgMgr.CR_INVALID_DEVICE_ID)
            {
                return new DeviceOpResult { Ok = true, Gone = true };
            }
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not find the device record: " + CfgMgr.Describe(cr) + ".");
            }

            CfgMgr.CM_Enable_DevNode(node, 0);
            return ClearDisabledFlag(node);
        }

        private static DeviceOpResult ClearDisabledFlag(int node)
        {
            if (!CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CONFIGFLAGS, out int flags))
            {
                // No flags stored means the device is not marked as disabled.
                return DeviceOpResult.Success();
            }
            if ((flags & CfgMgr.CONFIGFLAG_DISABLED) == 0) return DeviceOpResult.Success();

            byte[] cleared = BitConverter.GetBytes(flags & ~CfgMgr.CONFIGFLAG_DISABLED);
            int cr = CfgMgr.CM_Set_DevNode_Registry_PropertyW(node, CfgMgr.CM_DRP_CONFIGFLAGS, cleared,
                cleared.Length, 0);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not clear the disabled flag: " + CfgMgr.Describe(cr) + ".");
            }
            return DeviceOpResult.Success();
        }

        /// <summary>
        /// Takes a device out of the running system without disabling it. This works
        /// on devices Windows refuses to disable.
        /// </summary>
        public static DeviceOpResult Remove(string instanceId)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not find the device: " + CfgMgr.Describe(cr) + ".");
            }
            var veto = new System.Text.StringBuilder(260);
            cr = CfgMgr.CM_Query_And_Remove_SubTreeW(node, out int _, veto, veto.Capacity,
                CfgMgr.CM_REMOVE_UI_NOT_OK | CfgMgr.CM_REMOVE_NO_RESTART);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Windows refused to remove the device: " + CfgMgr.Describe(cr) + ".");
            }
            return DeviceOpResult.Success();
        }

        /// <summary>
        /// Switches a device off by the strongest method Windows allows for it.
        /// Most devices take a lasting disable. Windows refuses that for keyboards
        /// while their driver is running, so those are first removed from the
        /// running system. With the driver unloaded, the lasting disable is tried again.
        /// </summary>
        public static DeviceOpResult Block(string instanceId, out BlockMethod method)
        {
            method = BlockMethod.Disabled;
            DeviceOpResult disabled = Disable(instanceId);
            if (disabled.Ok) return disabled;
            if (disabled.Vetoed)
            {
                // Something has the device open: Explorer reading a new drive, a
                // virus scanner. Asking again later would leave the device usable in
                // the meantime, so take it off the bus instead.
                DeviceOpResult forced = ForceOff(instanceId);
                return forced.Ok ? forced : disabled;
            }
            if (!disabled.NotDisableable) return disabled;

            method = BlockMethod.Removed;
            DeviceOpResult removed = Remove(instanceId);
            if (!removed.Ok) return removed;

            if (Disable(instanceId).Ok) method = BlockMethod.Disabled;
            return DeviceOpResult.Success();
        }

        /// <summary>
        /// Switches off a device that Windows will not stop because a program is
        /// using it. The device is marked as disabled and its hub port is cycled,
        /// which is the same as pulling the plug and putting it back: no program
        /// gets a say, and Windows honours the mark when the device reappears.
        /// </summary>
        public static DeviceOpResult ForceOff(string instanceId)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not find the device: " + CfgMgr.Describe(cr) + ".");
            }

            int flags = CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CONFIGFLAGS, out int current) ? current : 0;
            byte[] marked = BitConverter.GetBytes(flags | CfgMgr.CONFIGFLAG_DISABLED);
            cr = CfgMgr.CM_Set_DevNode_Registry_PropertyW(node, CfgMgr.CM_DRP_CONFIGFLAGS, marked, marked.Length, 0);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not set the disabled flag: " + CfgMgr.Describe(cr) + ".");
            }

            if (!UsbDescriptors.CyclePort(node))
            {
                // Without the cycle the mark would only take effect at the next
                // replug. Take it back, so that state and ledger stay in step.
                ClearDisabledFlag(node);
                return DeviceOpResult.Fail("The hub did not accept the request to reconnect the device.");
            }

            // The device drops off the bus and comes back within a second or so.
            bool seenGone = false;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                System.Threading.Thread.Sleep(50);
                if (CfgMgr.CM_Locate_DevNodeW(out int again, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL) !=
                    CfgMgr.CR_SUCCESS)
                {
                    seenGone = true;
                    continue;
                }
                if (CfgMgr.CM_Get_DevNode_Status(out int status, out int problem, again, 0) != CfgMgr.CR_SUCCESS)
                {
                    continue;
                }
                if ((status & CfgMgr.DN_HAS_PROBLEM) != 0 && problem == CfgMgr.CM_PROB_DISABLED)
                {
                    return DeviceOpResult.Success();
                }
                if (seenGone && (status & CfgMgr.DN_STARTED) != 0)
                {
                    return DeviceOpResult.Fail("The device came back running after it was reconnected.");
                }
            }
            // It has not come back. The mark is in place for when it does.
            return seenGone
                ? DeviceOpResult.Success()
                : DeviceOpResult.Fail("The device did not leave the bus when its port was reset.");
        }

        /// <summary>
        /// Undoes Block, whichever method it used. Also works when the device has
        /// been unplugged since, in which case only its "disabled" mark is cleared.
        /// </summary>
        public static DeviceOpResult Unblock(string instanceId)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != CfgMgr.CR_SUCCESS) return Enable(instanceId);

            CfgMgr.CM_Enable_DevNode(node, 0);
            DeviceOpResult flag = ClearDisabledFlag(node);
            if (!flag.Ok) return flag;

            if (ReadProblem(node) == CfgMgr.CM_PROB_WILL_BE_REMOVED)
            {
                // Removed from the running system. Let Windows start it again.
                CfgMgr.CM_Setup_DevNode(node, CfgMgr.CM_SETUP_DEVNODE_RESET);
                CfgMgr.CM_Setup_DevNode(node, CfgMgr.CM_SETUP_DEVNODE_READY);
                if (CfgMgr.CM_Get_Parent(out int parent, node, 0) == CfgMgr.CR_SUCCESS)
                {
                    CfgMgr.CM_Reenumerate_DevNode(parent,
                        CfgMgr.CM_REENUMERATE_SYNCHRONOUS | CfgMgr.CM_REENUMERATE_RETRY_INSTALLATION);
                }
            }

            // Starting a device takes a moment. Give it a second before judging.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int problem = ReadProblem(node);
                if (problem != CfgMgr.CM_PROB_DISABLED && problem != CfgMgr.CM_PROB_WILL_BE_REMOVED)
                {
                    return DeviceOpResult.Success();
                }
                System.Threading.Thread.Sleep(100);
            }
            return DeviceOpResult.Fail("The device did not start again.");
        }

        private static int ReadProblem(int node)
        {
            if (CfgMgr.CM_Get_DevNode_Status(out int status, out int problem, node, 0) != CfgMgr.CR_SUCCESS) return 0;
            return (status & CfgMgr.DN_HAS_PROBLEM) != 0 ? problem : 0;
        }

        // ---------- installing a device afresh ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parentWindow);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr parentWindow,
            int flags, ref SP_DEVINFO_DATA data);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("newdev.dll", SetLastError = true)]
        private static extern bool DiUninstallDevice(IntPtr parentWindow, IntPtr set, ref SP_DEVINFO_DATA data,
            int flags, out bool needReboot);

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        /// <summary>
        /// Makes Windows forget a connected device and find it again. Windows
        /// decides whether a device may be installed only when it meets the device,
        /// so a device it refused stays refused until this is done, even after the
        /// rule that stopped it is gone. Nothing of value is lost: a device in that
        /// state has no driver and no settings yet.
        /// </summary>
        public static DeviceOpResult Reinstall(string instanceId)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not find the device: " + CfgMgr.Describe(cr) + ".");
            }
            // Look again right before acting. A device that started in the meantime
            // is installed, and forgetting it now would only take it away again.
            if (CfgMgr.CM_Get_DevNode_Status(out int status, out int _, node, 0) == CfgMgr.CR_SUCCESS &&
                (status & CfgMgr.DN_STARTED) != 0)
            {
                return new DeviceOpResult { Ok = true, Message = "already running" };
            }
            bool hasParent = CfgMgr.CM_Get_Parent(out int parent, node, 0) == CfgMgr.CR_SUCCESS;

            string how = Uninstall(instanceId, node);
            if (how == null)
            {
                return DeviceOpResult.Fail("Windows would not forget the device, so it could not be installed again.");
            }

            // The parent hub notices that the port holds a device it has no record
            // of, and Windows installs it as a new one.
            if (hasParent) CfgMgr.CM_Reenumerate_DevNode(parent, CfgMgr.CM_REENUMERATE_RETRY_INSTALLATION);
            return new DeviceOpResult { Ok = true, Message = how };
        }

        /// <summary>Returns which call removed the device record, or null when none did.</summary>
        private static string Uninstall(string instanceId, int node)
        {
            try
            {
                IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
                if (set != IntPtr.Zero && set != InvalidHandle)
                {
                    try
                    {
                        var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                        if (SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data) &&
                            DiUninstallDevice(IntPtr.Zero, set, ref data, 0, out bool _))
                        {
                            return "DiUninstallDevice";
                        }
                    }
                    finally
                    {
                        SetupDiDestroyDeviceInfoList(set);
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
            }

            try
            {
                if (CfgMgr.CM_Uninstall_DevNode(node, 0) == CfgMgr.CR_SUCCESS) return "CM_Uninstall_DevNode";
            }
            catch (EntryPointNotFoundException)
            {
            }
            return null;
        }

        /// <summary>
        /// Marks a device that is not connected as disabled. Windows honours the mark
        /// when the device is plugged in, so it comes back already switched off.
        /// Refuses a device that is connected: that one goes through Block.
        /// </summary>
        public static DeviceOpResult MarkAbsentDisabled(string instanceId)
        {
            if (CfgMgr.CM_Locate_DevNodeW(out int _, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL) ==
                CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("The device is connected.");
            }
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_PHANTOM);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                var gone = DeviceOpResult.Fail("Could not find the device record: " + CfgMgr.Describe(cr) + ".");
                gone.Gone = cr == CfgMgr.CR_NO_SUCH_DEVNODE || cr == CfgMgr.CR_INVALID_DEVICE_ID;
                return gone;
            }
            int flags = CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CONFIGFLAGS, out int current) ? current : 0;
            if ((flags & CfgMgr.CONFIGFLAG_DISABLED) != 0) return DeviceOpResult.Success();

            byte[] marked = BitConverter.GetBytes(flags | CfgMgr.CONFIGFLAG_DISABLED);
            cr = CfgMgr.CM_Set_DevNode_Registry_PropertyW(node, CfgMgr.CM_DRP_CONFIGFLAGS, marked, marked.Length, 0);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                return DeviceOpResult.Fail("Could not set the disabled flag: " + CfgMgr.Describe(cr) + ".");
            }
            return DeviceOpResult.Success();
        }

        /// <summary>
        /// True when the device is marked as disabled, whether or not it is connected.
        /// Returns null when Windows has no record of the device.
        /// </summary>
        public static bool? IsMarkedDisabled(string instanceId)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_PHANTOM);
            if (cr != CfgMgr.CR_SUCCESS) return null;
            if (!CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CONFIGFLAGS, out int flags)) return false;
            return (flags & CfgMgr.CONFIGFLAG_DISABLED) != 0;
        }
    }
}
