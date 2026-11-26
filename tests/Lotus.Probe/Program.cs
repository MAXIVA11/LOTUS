using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Lotus.Core;
using Lotus.Core.Native;

namespace Lotus.Probe
{
    /// <summary>
    /// Pokes at one device with the raw Windows calls LOTUS relies on and prints
    /// what came back. It exists to check assumptions on real hardware.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("usage: Lotus.Probe status|disable|enable|remove|restore|block|unblock <instance id>");
                Console.WriteLine("       Lotus.Probe watch <milliseconds>");
                Console.WriteLine("       Lotus.Probe find <text in instance id>");
                Console.WriteLine("       Lotus.Probe present <instance id>");
                return 2;
            }

            string command = args[0].ToLowerInvariant();
            string id = args.Length > 1 ? args[1] : "";
            switch (command)
            {
                case "status": return Status(id);
                case "disable": return Report("disable", DeviceControl.Disable(id), id);
                case "enable": return Report("enable", DeviceControl.Enable(id), id);
                case "block": return Report("block", DeviceControl.Block(id, out BlockMethod method), id, method.ToString());
                case "unblock": return Report("unblock", DeviceControl.Unblock(id), id);
                case "setflag": return SetFlag(id, args.Length > 2 && args[2] == "1");
                case "remove": return Remove(id);
                case "restore": return Restore(id);
                case "watch": return Watch(int.Parse(id));
                case "find": return Find(id);
                case "present": return Present(id);
                default: return 2;
            }
        }

        /// <summary>Sets or clears the "disabled" mark on a device record, connected or not.</summary>
        private static int SetFlag(string id, bool on)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, id, CfgMgr.CM_LOCATE_DEVNODE_PHANTOM);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                Console.WriteLine("setflag: locate failed cr=0x" + cr.ToString("X2"));
                return 1;
            }
            CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CONFIGFLAGS, out int flags);
            int wanted = on ? flags | CfgMgr.CONFIGFLAG_DISABLED : flags & ~CfgMgr.CONFIGFLAG_DISABLED;
            byte[] data = BitConverter.GetBytes(wanted);
            cr = CfgMgr.CM_Set_DevNode_Registry_PropertyW(node, CfgMgr.CM_DRP_CONFIGFLAGS, data, data.Length, 0);
            Console.WriteLine("setflag: cr=0x" + cr.ToString("X2") + " wanted=0x" + wanted.ToString("X"));
            Status(id);
            return cr == CfgMgr.CR_SUCCESS ? 0 : 1;
        }

        private static int Report(string what, DeviceOpResult result, string id, string extra = null)
        {
            Console.WriteLine(what + ": ok=" + result.Ok + " gone=" + result.Gone +
                              (extra != null ? " method=" + extra : "") + " msg=\"" + result.Message + "\"");
            Status(id);
            return result.Ok ? 0 : 1;
        }

        private static int Status(string id)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, id, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            bool present = cr == CfgMgr.CR_SUCCESS;
            if (!present) cr = CfgMgr.CM_Locate_DevNodeW(out node, id, CfgMgr.CM_LOCATE_DEVNODE_PHANTOM);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                Console.WriteLine("status: locate failed cr=0x" + cr.ToString("X2") + " (" + CfgMgr.Describe(cr) + ")");
                return 1;
            }
            int scr = CfgMgr.CM_Get_DevNode_Status(out int status, out int problem, node, 0);
            bool hasFlags = CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_CONFIGFLAGS, out int flags);
            Console.WriteLine("status: located=" + (present ? "live" : "phantom") +
                              " statusCr=0x" + scr.ToString("X2") +
                              " dn=0x" + status.ToString("X8") +
                              " started=" + ((status & CfgMgr.DN_STARTED) != 0) +
                              " hasProblem=" + ((status & CfgMgr.DN_HAS_PROBLEM) != 0) +
                              " problem=" + problem +
                              " disableable=" + ((status & CfgMgr.DN_DISABLEABLE) != 0) +
                              " configFlags=" + (hasFlags ? "0x" + flags.ToString("X") : "none"));
            return 0;
        }

        private static int Present(string id)
        {
            List<string> ids = CfgMgr.GetDeviceIds("USB",
                CfgMgr.CM_GETIDLIST_FILTER_ENUMERATOR | CfgMgr.CM_GETIDLIST_FILTER_PRESENT);
            bool listed = false;
            foreach (string candidate in ids)
            {
                if (candidate.Equals(id, StringComparison.OrdinalIgnoreCase)) listed = true;
            }
            bool scanned = false;
            foreach (UsbDevice device in DeviceScanner.Scan())
            {
                if (device.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    scanned = true;
                    Console.WriteLine("present: scanner sees it, started=" + device.Started + " problem=" +
                                      device.Problem + " kinds=" + device.Kinds + " known=" + device.FunctionsKnown);
                }
            }
            Console.WriteLine("present: inPresentList=" + listed + " inScan=" + scanned);
            return 0;
        }

        private static int Remove(string id)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, id, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            if (cr != CfgMgr.CR_SUCCESS)
            {
                Console.WriteLine("remove: locate failed cr=0x" + cr.ToString("X2"));
                return 1;
            }
            var veto = new StringBuilder(260);
            cr = CfgMgr.CM_Query_And_Remove_SubTreeW(node, out int vetoType, veto, veto.Capacity,
                CfgMgr.CM_REMOVE_UI_NOT_OK | CfgMgr.CM_REMOVE_NO_RESTART);
            Console.WriteLine("remove: cr=0x" + cr.ToString("X2") + " (" + CfgMgr.Describe(cr) + ") vetoType=" +
                              vetoType + " vetoName=\"" + veto + "\"");
            Status(id);
            return cr == CfgMgr.CR_SUCCESS ? 0 : 1;
        }

        private static int Restore(string id)
        {
            int cr = CfgMgr.CM_Locate_DevNodeW(out int node, id, CfgMgr.CM_LOCATE_DEVNODE_NORMAL);
            Console.WriteLine("restore: locate normal cr=0x" + cr.ToString("X2"));
            if (cr != CfgMgr.CR_SUCCESS)
            {
                cr = CfgMgr.CM_Locate_DevNodeW(out node, id, CfgMgr.CM_LOCATE_DEVNODE_PHANTOM);
                Console.WriteLine("restore: locate phantom cr=0x" + cr.ToString("X2"));
            }
            if (cr == CfgMgr.CR_SUCCESS)
            {
                int reset = CfgMgr.CM_Setup_DevNode(node, CfgMgr.CM_SETUP_DEVNODE_RESET);
                Console.WriteLine("restore: setup reset cr=0x" + reset.ToString("X2"));
                int ready = CfgMgr.CM_Setup_DevNode(node, CfgMgr.CM_SETUP_DEVNODE_READY);
                Console.WriteLine("restore: setup ready cr=0x" + ready.ToString("X2"));
                if (CfgMgr.CM_Get_Parent(out int parent, node, 0) == CfgMgr.CR_SUCCESS)
                {
                    int re = CfgMgr.CM_Reenumerate_DevNode(parent,
                        CfgMgr.CM_REENUMERATE_SYNCHRONOUS | CfgMgr.CM_REENUMERATE_RETRY_INSTALLATION);
                    Console.WriteLine("restore: reenumerate parent cr=0x" + re.ToString("X2"));
                }
            }
            Thread.Sleep(1500);
            return Status(id);
        }

        private static int Watch(int milliseconds)
        {
            int count = 0;
            using (var watcher = new DeviceWatcher((action, id) =>
                   {
                       Interlocked.Increment(ref count);
                       Console.WriteLine("  " + DateTime.Now.ToString("HH:mm:ss.fff") + "  action=" + action + "  " + id);
                   }))
            {
                bool started = watcher.Start();
                Console.WriteLine("watch: started=" + started);
                Thread.Sleep(milliseconds);
            }
            Console.WriteLine("watch: notifications=" + count);
            return 0;
        }

        private static int Find(string text)
        {
            foreach (string id in CfgMgr.GetDeviceIds(null, CfgMgr.CM_GETIDLIST_FILTER_PRESENT))
            {
                if (id.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) Console.WriteLine(id);
            }
            return 0;
        }
    }
}
