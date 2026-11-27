using System;
using System.Collections.Generic;
using Lotus.Core.Native;

namespace Lotus.Core
{
    /// <summary>
    /// Finds the keyboards and mice Windows has that LOTUS does not manage: the
    /// keyboard and touchpad of a laptop, a keyboard on a PS/2 port, one that is
    /// connected over Bluetooth. LOTUS asks this before it concludes that the
    /// computer has no working keyboard or mouse left.
    /// </summary>
    public static class WindowsInput
    {
        private const string KeyboardClass = "{4D36E96B-E325-11CE-BFC1-08002BE10318}";
        private const string MouseClass = "{4D36E96F-E325-11CE-BFC1-08002BE10318}";

        /// <summary>The instance IDs of the working keyboards and mice that are not USB devices.</summary>
        public static List<string> FindBesidesUsb()
        {
            var found = new List<string>();
            foreach (string deviceClass in new[] { KeyboardClass, MouseClass })
            {
                List<string> ids = CfgMgr.GetDeviceIds(deviceClass,
                    CfgMgr.CM_GETIDLIST_FILTER_CLASS | CfgMgr.CM_GETIDLIST_FILTER_PRESENT);
                foreach (string id in ids)
                {
                    if (CfgMgr.CM_Locate_DevNodeW(out int node, id, CfgMgr.CM_LOCATE_DEVNODE_NORMAL) != CfgMgr.CR_SUCCESS)
                    {
                        continue;
                    }
                    if (CfgMgr.CM_Get_DevNode_Status(out int status, out int _, node, 0) != CfgMgr.CR_SUCCESS ||
                        (status & CfgMgr.DN_STARTED) == 0)
                    {
                        continue;
                    }
                    if (CountsBesidesUsb(Chain(id, node))) found.Add(id);
                }
            }
            return found;
        }

        /// <summary>The device and everything above it in the device tree, nearest first.</summary>
        private static List<string> Chain(string id, int node)
        {
            var chain = new List<string> { id };
            int current = node;
            for (int depth = 0; depth < 24; depth++)
            {
                if (CfgMgr.CM_Get_Parent(out int parent, current, 0) != CfgMgr.CR_SUCCESS) break;
                string parentId = CfgMgr.GetDeviceId(parent);
                if (string.IsNullOrEmpty(parentId)) break;
                chain.Add(parentId);
                current = parent;
            }
            return chain;
        }

        /// <summary>
        /// Decides from where a keyboard or mouse hangs in the device tree.
        ///
        /// A USB keyboard or mouse does not count: LOTUS judges it with the other
        /// USB devices. One that comes in over a Bluetooth radio counts, also when
        /// the radio itself is a USB device, because what LOTUS manages is the
        /// radio and not the keyboard behind it. A keyboard that exists in software
        /// only, such as the one Remote Desktop adds to every PC, does not count:
        /// nobody can type on it at the computer.
        /// </summary>
        public static bool CountsBesidesUsb(IList<string> chain)
        {
            if (chain == null || chain.Count == 0) return false;
            bool bluetooth = false;
            string transport = null;
            foreach (string link in chain)
            {
                string id = (link ?? "").ToUpperInvariant();
                if (id.StartsWith(@"BTHENUM\", StringComparison.Ordinal) || id.StartsWith(@"BTHLE\", StringComparison.Ordinal) ||
                    id.StartsWith(@"BTHLEDEVICE\", StringComparison.Ordinal) || id.StartsWith(@"BTH\", StringComparison.Ordinal))
                {
                    bluetooth = true;
                }
                if (id.StartsWith(@"USB\", StringComparison.Ordinal)) return bluetooth;
                // The first thing above the HID layer says how the device is connected.
                if (transport == null && !id.StartsWith(@"HID\", StringComparison.Ordinal)) transport = id;
            }
            if (transport == null) return false;
            return !transport.StartsWith(@"ROOT\", StringComparison.Ordinal);
        }
    }
}
