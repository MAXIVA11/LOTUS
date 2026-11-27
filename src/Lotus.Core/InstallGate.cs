using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>
    /// What Windows is told about installing USB devices: which hardware and
    /// compatible IDs it must refuse, and which device instances get through
    /// anyway.
    /// </summary>
    public sealed class InstallPlan
    {
        public readonly List<string> Deny = new List<string>();
        public readonly List<string> AllowInstances = new List<string>();

        public InstallPlan(IEnumerable<string> deny, IEnumerable<string> allowInstances)
        {
            // The class IDs keep the spelling Windows itself uses for them.
            Fill(Deny, deny, false);
            Fill(AllowInstances, allowInstances, true);
        }

        private static void Fill(List<string> into, IEnumerable<string> from, bool upper)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (from != null)
            {
                foreach (string id in from)
                {
                    string clean = (id ?? "").Trim();
                    if (upper) clean = clean.ToUpperInvariant();
                    if (clean.Length > 0 && seen.Add(clean)) into.Add(clean);
                }
            }
            into.Sort(StringComparer.Ordinal);
        }
    }

    /// <summary>The IDs LOTUS asks Windows to refuse.</summary>
    public static class InstallRules
    {
        /// <summary>
        /// Every device and every interface on a USB port carries one of these
        /// compatible IDs, whatever it is. Devices on other buses do not, and
        /// neither do the drives, keyboards and volumes below a USB device.
        /// </summary>
        public static List<string> EveryUsbDevice()
        {
            var ids = new List<string> { @"USB\COMPOSITE" };
            for (int code = 0; code <= 0xFF; code++)
            {
                ids.Add(@"USB\Class_" + code.ToString("X2"));
                ids.Add(@"USB\DevClass_" + code.ToString("X2"));
            }
            return ids;
        }

        /// <summary>Mass storage, cameras and phones that show their files.</summary>
        public static List<string> UsbStorage()
        {
            return new List<string> { @"USB\Class_08", @"USB\Class_06", @"USB\MS_COMP_MTP" };
        }

        /// <summary>The IDs to refuse under these settings, or null when nothing is to be refused.</summary>
        public static List<string> DenyFor(bool armedAndStrict, bool storageBlocked, bool pnpBlocked)
        {
            if (!armedAndStrict) return null;
            if (pnpBlocked) return EveryUsbDevice();
            if (storageBlocked) return UsbStorage();
            return null;
        }
    }

    /// <summary>
    /// The door Windows itself keeps for new devices. With it shut, a USB device
    /// Windows has not seen before is refused before any driver is installed for
    /// it, so it never starts. LOTUS then opens the door for single devices the
    /// policy allows.
    /// </summary>
    public interface IInstallGate
    {
        /// <summary>False on a Windows version that cannot do this.</summary>
        bool Supported { get; }
        /// <summary>LOTUS's restrictions are in force.</summary>
        bool Owned { get; }
        /// <summary>Someone else's restrictions are in force. LOTUS leaves them alone.</summary>
        bool Foreign { get; }
        bool Matches(InstallPlan plan);
        void Apply(InstallPlan plan);
        /// <summary>Lets one more device instance through. Does nothing unless LOTUS's restrictions are in force.</summary>
        void AllowInstance(string instanceId);
        /// <summary>Takes LOTUS's restrictions away. Never touches anyone else's.</summary>
        void Remove();
    }

    /// <summary>A gate that is never there. Used where LOTUS must not touch Windows policy.</summary>
    public sealed class NoInstallGate : IInstallGate
    {
        public bool Supported => false;
        public bool Owned => false;
        public bool Foreign => false;
        public bool Matches(InstallPlan plan) { return false; }
        public void Apply(InstallPlan plan) { }
        public void AllowInstance(string instanceId) { }
        public void Remove() { }
    }
}
