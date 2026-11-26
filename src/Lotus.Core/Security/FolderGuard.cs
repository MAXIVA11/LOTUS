using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Lotus.Core
{
    /// <summary>
    /// Sets and checks the permissions on the LOTUS data folder. Any Windows user
    /// may create folders under C:\ProgramData, so a folder that is already there
    /// when LOTUS is installed cannot be taken at face value: someone without
    /// administrator rights may own it and keep the right to change what is inside.
    /// </summary>
    public static class FolderGuard
    {
        private static readonly SecurityIdentifier LocalSystem =
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier Administrators =
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier Users =
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        /// <summary>
        /// The data folder itself: the service has full control, administrators and
        /// everyone else can read. The activity log lives here, and the notifier,
        /// which has no special rights, has to read it.
        /// </summary>
        public static DirectorySecurity PublicFolderRules()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(Administrators);
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Inherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.ReadAndExecute, Inherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, Inherit,
                PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        /// <summary>
        /// The folder with the policy store: the service has full control and
        /// administrators can read. Nobody else gets in, because the trusted list
        /// tells an attacker exactly which device to imitate.
        /// </summary>
        public static DirectorySecurity SecretFolderRules()
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(Administrators);
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, Inherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.ReadAndExecute, Inherit,
                PropagationFlags.None, AccessControlType.Allow));
            return security;
        }

        /// <summary>The seal key: only the service account may read it.</summary>
        public static FileSecurity KeyFileRules()
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(Administrators);
            security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl,
                AccessControlType.Allow));
            return security;
        }

        public static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Says what is wrong with a folder that is supposed to belong to LOTUS, or
        /// returns null when it can be trusted: a real folder, owned by the system
        /// or by the Administrators group, that ordinary users cannot write to.
        /// </summary>
        public static string Inspect(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return "The folder does not exist.";
                if (IsReparsePoint(directory)) return "The folder is a link to somewhere else.";

                DirectorySecurity security = Directory.GetAccessControl(directory,
                    AccessControlSections.Owner | AccessControlSections.Access);
                var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
                if (owner == null || !(owner.Equals(LocalSystem) || owner.Equals(Administrators) ||
                                       owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
                {
                    return "The folder is owned by an account without administrator rights.";
                }

                const FileSystemRights writing =
                    FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
                    FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
                    FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
                    FileSystemRights.TakeOwnership;
                foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow) continue;
                    var who = (SecurityIdentifier)rule.IdentityReference;
                    if (who.Equals(LocalSystem) || who.Equals(Administrators)) continue;
                    if ((rule.FileSystemRights & writing) != 0)
                    {
                        return "The folder can be changed by an account without administrator rights.";
                    }
                }
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException || ex is SystemException)
            {
                return "The folder's permissions could not be read. " + ex.Message;
            }
        }

        /// <summary>
        /// Creates a folder with the given rules in one step, so there is no moment
        /// in which it exists with looser permissions. Returns false when the folder
        /// was already there, in which case nothing was changed.
        /// </summary>
        public static bool CreateNew(string directory, DirectorySecurity rules)
        {
            if (Directory.Exists(directory) || File.Exists(directory)) return false;
            Directory.CreateDirectory(directory, rules);
            return true;
        }

        /// <summary>Applies the rules to an existing folder and everything in it.</summary>
        /// <param name="skip">Names directly inside the folder that keep their own, stricter rules.</param>
        public static void Apply(string directory, DirectorySecurity rules, params string[] skip)
        {
            Directory.SetAccessControl(directory, rules);
            foreach (string entry in Directory.GetFileSystemEntries(directory))
            {
                // Never follow a link out of the folder.
                if (IsReparsePoint(entry)) continue;
                if (skip != null && Array.Exists(skip, name =>
                        string.Equals(name, Path.GetFileName(entry), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (Directory.Exists(entry))
                {
                    var inherit = new DirectorySecurity();
                    inherit.SetAccessRuleProtection(false, false);
                    inherit.SetOwner(Administrators);
                    Directory.SetAccessControl(entry, inherit);
                    ResetChildren(entry);
                }
                else
                {
                    var inherit = new FileSecurity();
                    inherit.SetAccessRuleProtection(false, false);
                    inherit.SetOwner(Administrators);
                    File.SetAccessControl(entry, inherit);
                }
            }
        }

        private static void ResetChildren(string directory)
        {
            foreach (string entry in Directory.GetFileSystemEntries(directory))
            {
                if (IsReparsePoint(entry)) continue;
                if (Directory.Exists(entry))
                {
                    var inherit = new DirectorySecurity();
                    inherit.SetAccessRuleProtection(false, false);
                    inherit.SetOwner(Administrators);
                    Directory.SetAccessControl(entry, inherit);
                    ResetChildren(entry);
                }
                else
                {
                    var inherit = new FileSecurity();
                    inherit.SetAccessRuleProtection(false, false);
                    inherit.SetOwner(Administrators);
                    File.SetAccessControl(entry, inherit);
                }
            }
        }

        /// <summary>
        /// Removes a folder that cannot be trusted, without following any link in it.
        /// Takes ownership first where that is needed, which requires the caller to
        /// be an administrator.
        /// </summary>
        public static void RemoveUntrusted(string directory)
        {
            if (!Directory.Exists(directory)) return;
            EnablePrivilege("SeTakeOwnershipPrivilege");
            EnablePrivilege("SeRestorePrivilege");
            EnablePrivilege("SeBackupPrivilege");
            RemoveTree(directory);
        }

        private static void RemoveTree(string directory)
        {
            if (IsReparsePoint(directory))
            {
                // A junction or symbolic link: remove the link, leave its target alone.
                Directory.Delete(directory, false);
                return;
            }
            TakeOver(directory, true);
            foreach (string entry in Directory.GetFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory)
                {
                    RemoveTree(entry);
                }
                else
                {
                    if ((attributes & FileAttributes.ReparsePoint) == 0) TakeOver(entry, false);
                    File.SetAttributes(entry, FileAttributes.Normal);
                    File.Delete(entry);
                }
            }
            Directory.Delete(directory, false);
        }

        private static void TakeOver(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    var owner = new DirectorySecurity();
                    owner.SetOwner(Administrators);
                    Directory.SetAccessControl(path, owner);
                    var access = new DirectorySecurity();
                    access.SetAccessRuleProtection(true, false);
                    access.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl,
                        AccessControlType.Allow));
                    Directory.SetAccessControl(path, access);
                }
                else
                {
                    var owner = new FileSecurity();
                    owner.SetOwner(Administrators);
                    File.SetAccessControl(path, owner);
                    var access = new FileSecurity();
                    access.SetAccessRuleProtection(true, false);
                    access.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl,
                        AccessControlType.Allow));
                    File.SetAccessControl(path, access);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException)
            {
                // The delete that follows reports the real problem if there is one.
            }
        }

        // ---------- privileges ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValueW(string system, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state,
            uint length, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>Switches on a privilege the account holds but does not use by default.</summary>
        public static bool EnablePrivilege(string name)
        {
            const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            {
                return false;
            }
            try
            {
                if (!LookupPrivilegeValueW(null, name, out LUID luid)) return false;
                var state = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
    }
}
