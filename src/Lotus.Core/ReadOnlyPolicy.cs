using System;
using Microsoft.Win32;

namespace Lotus.Core
{
    /// <summary>Switches removable storage between writable and read-only.</summary>
    public interface IReadOnlyPolicy
    {
        bool IsApplied();
        void Apply();
        void Remove();
    }

    /// <summary>
    /// Read-only mode uses Windows' own "Removable Storage Access" policy, the one
    /// Group Policy writes under HKLM\SOFTWARE\Policies. Windows enforces it in the
    /// storage stack, so it also covers card readers and drives LOTUS trusts.
    /// </summary>
    public sealed class RegistryReadOnlyPolicy : IReadOnlyPolicy
    {
        public const string BaseKey = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices";
        public const string ValueName = "Deny_Write";

        /// <summary>The device classes Windows lets this policy cover.</summary>
        public static readonly string[] ClassKeys =
        {
            "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}", // removable disks
            "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}", // CD and DVD
            "{53f56311-b6bf-11d0-94f2-00a0c91efb8b}", // floppy drives
            "{6AC27878-A6FA-4155-BA85-F98F491D4F33}", // portable devices (phones, cameras)
            "{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}"  // portable devices, second class
        };

        private static RegistryKey OpenMachine()
        {
            // Always use the 64-bit view on 64-bit Windows. The 32-bit view is redirected
            // to a place the storage stack never reads.
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        }

        public bool IsApplied()
        {
            using (RegistryKey machine = OpenMachine())
            {
                foreach (string cls in ClassKeys)
                {
                    using (RegistryKey key = machine.OpenSubKey(BaseKey + "\\" + cls, false))
                    {
                        if (key == null) return false;
                        object value = key.GetValue(ValueName);
                        if (!(value is int number) || number != 1) return false;
                    }
                }
            }
            return true;
        }

        public void Apply()
        {
            using (RegistryKey machine = OpenMachine())
            {
                foreach (string cls in ClassKeys)
                {
                    using (RegistryKey key = machine.CreateSubKey(BaseKey + "\\" + cls))
                    {
                        if (key == null) throw new InvalidOperationException("Could not create the policy key.");
                        key.SetValue(ValueName, 1, RegistryValueKind.DWord);
                    }
                }
            }
        }

        public void Remove()
        {
            using (RegistryKey machine = OpenMachine())
            {
                foreach (string cls in ClassKeys)
                {
                    string path = BaseKey + "\\" + cls;
                    bool empty = false;
                    using (RegistryKey key = machine.OpenSubKey(path, true))
                    {
                        if (key == null) continue;
                        key.DeleteValue(ValueName, false);
                        empty = key.ValueCount == 0 && key.SubKeyCount == 0;
                    }
                    // Leave the key alone if someone else keeps other settings in it.
                    if (empty) machine.DeleteSubKey(path, false);
                }
                using (RegistryKey baseKey = machine.OpenSubKey(BaseKey, false))
                {
                    if (baseKey == null || baseKey.ValueCount != 0 || baseKey.SubKeyCount != 0) return;
                }
                machine.DeleteSubKey(BaseKey, false);
            }
        }
    }
}
