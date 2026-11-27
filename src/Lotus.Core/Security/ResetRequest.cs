using System;
using System.IO;
using Microsoft.Win32;

namespace Lotus.Core
{
    /// <summary>
    /// How setup tells the service to throw away a policy store that cannot be
    /// trusted. Setup cannot touch the store itself, because only the service may
    /// write there, so it leaves a note in the registry. The service acts on the
    /// note only when the store really is damaged, and removes it either way.
    /// </summary>
    public static class ResetRequest
    {
        private const string KeyPath = @"SOFTWARE\LOTUS";
        private const string ValueName = "ResetStore";

        public static void Ask()
        {
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = machine.CreateSubKey(KeyPath))
            {
                key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            }
        }

        /// <summary>True when a reset was asked for. The request is gone afterwards.</summary>
        public static bool Take()
        {
            try
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (RegistryKey key = machine.OpenSubKey(KeyPath, true))
                {
                    if (key == null) return false;
                    bool asked = key.GetValue(ValueName) is int value && value == 1;
                    key.DeleteValue(ValueName, false);
                    return asked;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        /// <summary>Removes the registry key LOTUS uses for this. Part of uninstalling.</summary>
        public static void Forget()
        {
            try
            {
                using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    machine.DeleteSubKeyTree(KeyPath, false);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.SecurityException || ex is ArgumentException)
            {
            }
        }
    }
}
