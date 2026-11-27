using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Lotus.Core
{
    /// <summary>
    /// The device installation restrictions built into Windows, the same ones
    /// Group Policy sets under "Device Installation Restrictions". Windows reads
    /// them every time it is about to install a device. LOTUS uses the deny list
    /// for hardware and compatible IDs, the allow list for device instances, and
    /// the layered order of evaluation in which an allowed instance beats a
    /// denied ID.
    /// </summary>
    public sealed class RegistryInstallGate : IInstallGate
    {
        public const string DefaultPath = @"SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions";
        /// <summary>Layered evaluation arrived with Windows 10 21H2 and is in every Windows 11.</summary>
        private const int FirstBuildWithLayers = 19044;

        private const string Marker = "ManagedByLOTUS";
        private const string DenyList = "DenyDeviceIDs";
        private const string AllowList = "AllowInstanceIDs";
        private const string Layered = "AllowDenyLayered";
        private const string Retroactive = "DenyDeviceIDsRetroactive";

        /// <summary>Settings that restrict installation. Any of them without LOTUS's mark belongs to someone else.</summary>
        private static readonly string[] RestrictingValues =
        {
            "DenyDeviceIDs", "DenyInstanceIDs", "DenyDeviceClasses", "DenyUnspecified", "DenyRemovableDevices",
            "AllowDeviceIDs", "AllowInstanceIDs", "AllowDeviceClasses"
        };

        private readonly RegistryHive hive;
        private readonly string path;
        private readonly bool checkWindowsVersion;

        public RegistryInstallGate() : this(RegistryHive.LocalMachine, DefaultPath, true)
        {
        }

        /// <summary>Tests point this at a key of their own.</summary>
        public RegistryInstallGate(RegistryHive hive, string path, bool checkWindowsVersion)
        {
            this.hive = hive;
            this.path = path;
            this.checkWindowsVersion = checkWindowsVersion;
        }

        private RegistryKey OpenBase()
        {
            return RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        }

        public bool Supported
        {
            get
            {
                if (!checkWindowsVersion) return true;
                try
                {
                    using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (RegistryKey key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    {
                        string build = key?.GetValue("CurrentBuildNumber") as string;
                        return int.TryParse(build, out int number) && number >= FirstBuildWithLayers;
                    }
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException ||
                                           ex is System.Security.SecurityException)
                {
                    return false;
                }
            }
        }

        public bool Owned
        {
            get
            {
                using (RegistryKey root = OpenBase())
                using (RegistryKey key = root.OpenSubKey(path))
                {
                    return key != null && IsOne(key, Marker);
                }
            }
        }

        public bool Foreign
        {
            get
            {
                using (RegistryKey root = OpenBase())
                using (RegistryKey key = root.OpenSubKey(path))
                {
                    if (key == null || IsOne(key, Marker)) return false;
                    foreach (string name in RestrictingValues)
                    {
                        if (key.GetValue(name) is int value && value != 0) return true;
                    }
                    return false;
                }
            }
        }

        private static bool IsOne(RegistryKey key, string name)
        {
            return key.GetValue(name) is int value && value == 1;
        }

        private static HashSet<string> ReadList(RegistryKey parent, string name)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (RegistryKey list = parent.OpenSubKey(name))
            {
                if (list == null) return ids;
                foreach (string valueName in list.GetValueNames())
                {
                    if (list.GetValue(valueName) is string id && id.Length > 0) ids.Add(id);
                }
            }
            return ids;
        }

        public bool Matches(InstallPlan plan)
        {
            using (RegistryKey root = OpenBase())
            using (RegistryKey key = root.OpenSubKey(path))
            {
                if (key == null || !IsOne(key, Marker) || !IsOne(key, Layered) || !IsOne(key, DenyList) ||
                    !IsOne(key, AllowList))
                {
                    return false;
                }
                if (key.GetValue(Retroactive) is int retroactive && retroactive != 0) return false;
                return ReadList(key, DenyList).SetEquals(plan.Deny) &&
                       ReadList(key, AllowList).SetEquals(plan.AllowInstances);
            }
        }

        public void Apply(InstallPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            using (RegistryKey root = OpenBase())
            using (RegistryKey key = root.CreateSubKey(path))
            {
                if (!IsOne(key, Marker))
                {
                    foreach (string name in RestrictingValues)
                    {
                        if (key.GetValue(name) is int value && value != 0)
                        {
                            throw new InvalidOperationException(
                                "This PC already has device installation restrictions that LOTUS did not set.");
                        }
                    }
                }
                key.SetValue(Marker, 1, RegistryValueKind.DWord);
                key.SetValue(Layered, 1, RegistryValueKind.DWord);
                // The exceptions go in before the rule, so there is no moment in
                // which an allowed device would be refused.
                if (!ReadList(key, AllowList).SetEquals(plan.AllowInstances)) WriteList(key, AllowList, plan.AllowInstances);
                key.SetValue(AllowList, 1, RegistryValueKind.DWord);
                if (!ReadList(key, DenyList).SetEquals(plan.Deny)) WriteList(key, DenyList, plan.Deny);
                key.SetValue(Retroactive, 0, RegistryValueKind.DWord);
                key.SetValue(DenyList, 1, RegistryValueKind.DWord);
            }
        }

        private static void WriteList(RegistryKey parent, string name, List<string> ids)
        {
            parent.DeleteSubKeyTree(name, false);
            using (RegistryKey list = parent.CreateSubKey(name))
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    list.SetValue((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), ids[i],
                        RegistryValueKind.String);
                }
            }
        }

        public void AllowInstance(string instanceId)
        {
            string id = (instanceId ?? "").Trim().ToUpperInvariant();
            if (id.Length == 0) return;
            using (RegistryKey root = OpenBase())
            using (RegistryKey key = root.OpenSubKey(path, true))
            {
                if (key == null || !IsOne(key, Marker)) return;
                using (RegistryKey list = key.CreateSubKey(AllowList))
                {
                    var names = new HashSet<string>(list.GetValueNames(), StringComparer.OrdinalIgnoreCase);
                    foreach (string valueName in names)
                    {
                        if (list.GetValue(valueName) is string existing &&
                            string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                    int next = names.Count + 1;
                    while (names.Contains(next.ToString(System.Globalization.CultureInfo.InvariantCulture))) next++;
                    list.SetValue(next.ToString(System.Globalization.CultureInfo.InvariantCulture), id,
                        RegistryValueKind.String);
                }
                key.SetValue(AllowList, 1, RegistryValueKind.DWord);
            }
        }

        public void Remove()
        {
            using (RegistryKey root = OpenBase())
            {
                using (RegistryKey key = root.OpenSubKey(path, true))
                {
                    if (key == null || !IsOne(key, Marker)) return;
                    // The rule goes first and the exceptions last.
                    key.DeleteValue(DenyList, false);
                    key.DeleteSubKeyTree(DenyList, false);
                    key.DeleteValue(Retroactive, false);
                    key.DeleteValue(AllowList, false);
                    key.DeleteSubKeyTree(AllowList, false);
                    key.DeleteValue(Layered, false);
                    key.DeleteValue(Marker, false);
                }
                DeleteIfEmpty(root, path);
                int slash = path.LastIndexOf('\\');
                if (slash > 0) DeleteIfEmpty(root, path.Substring(0, slash));
            }
        }

        /// <summary>Windows has no such key on a PC without policies, so do not leave an empty one behind.</summary>
        private static void DeleteIfEmpty(RegistryKey root, string keyPath)
        {
            using (RegistryKey key = root.OpenSubKey(keyPath))
            {
                if (key == null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
            }
            root.DeleteSubKey(keyPath, false);
        }
    }
}
