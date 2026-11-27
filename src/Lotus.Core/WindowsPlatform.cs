using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lotus.Core
{
    /// <summary>LOTUS on Windows: Configuration Manager, the registry, a named pipe and NTFS permissions.</summary>
    public sealed class WindowsPlatform : GuardPlatform
    {
        public override IDeviceBackend CreateBackend()
        {
            return new WindowsDeviceBackend();
        }

        public override IReadOnlyPolicy CreateReadOnly()
        {
            return new RegistryReadOnlyPolicy();
        }

        public override IInstallGate CreateInstallGate(IDeviceBackend backend)
        {
            return new RegistryInstallGate();
        }

        public override IControlServer CreateControlServer(string name,
            Func<Dictionary<string, object>, Dictionary<string, object>> handler, Action<string> onProblem,
            bool restricted)
        {
            return new GuardPipeServer(name, handler, onProblem, restricted);
        }

        public override IDisposable WatchDevices(Action<int, string> onChange)
        {
            var watcher = new DeviceWatcher((action, instanceId) =>
            {
                bool usb = instanceId != null && instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
                if (usb && (action == DeviceChange.Enumerated || action == DeviceChange.Removed))
                {
                    // The same ID can come back as a different device, so read it afresh.
                    DeviceScanner.Forget(instanceId);
                }
                onChange(action, instanceId);
            });
            if (watcher.Start()) return watcher;
            watcher.Dispose();
            return null;
        }

        /// <summary>
        /// Setup has normally done all of this already. The service repeats it,
        /// because it is the one that trusts what these folders contain.
        /// </summary>
        public override void PrepareFolders(Action<string> onError)
        {
            try
            {
                if (Directory.Exists(LotusPaths.DataDir) && FolderGuard.IsReparsePoint(LotusPaths.DataDir))
                {
                    Directory.Delete(LotusPaths.DataDir, false);
                }
                if (!FolderGuard.CreateNew(LotusPaths.DataDir, FolderGuard.PublicFolderRules()))
                {
                    FolderGuard.Apply(LotusPaths.DataDir, FolderGuard.PublicFolderRules(), "secure");
                }

                if (Directory.Exists(LotusPaths.SecureDir) && FolderGuard.IsReparsePoint(LotusPaths.SecureDir))
                {
                    Directory.Delete(LotusPaths.SecureDir, false);
                }
                if (!FolderGuard.CreateNew(LotusPaths.SecureDir, FolderGuard.SecretFolderRules()))
                {
                    FolderGuard.Apply(LotusPaths.SecureDir, FolderGuard.SecretFolderRules(), "seal.key");
                }
                string key = Path.Combine(LotusPaths.SecureDir, "seal.key");
                if (File.Exists(key) && !FolderGuard.IsReparsePoint(key))
                {
                    File.SetAccessControl(key, FolderGuard.KeyFileRules());
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException)
            {
                Directory.CreateDirectory(LotusPaths.DataDir);
                Directory.CreateDirectory(LotusPaths.SecureDir);
                onError("The permissions on the LOTUS data folder could not be set. " + ex.Message);
            }
        }

        public override void ConfigureStore(SecureStore store)
        {
            store.KeyFileSecurity = FolderGuard.KeyFileRules;
        }

        public override void HandBackFolders()
        {
            try
            {
                var open = new DirectorySecurity();
                open.SetAccessRuleProtection(true, false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                open.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                open.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                FolderGuard.Apply(LotusPaths.DataDir, open);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException)
            {
            }
        }

        public override bool TakeResetRequest()
        {
            return ResetRequest.Take();
        }
    }
}
