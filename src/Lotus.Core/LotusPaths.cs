using System;
using System.IO;

namespace Lotus.Core
{
    /// <summary>Names and file locations shared by the service, the app and setup.</summary>
    public static class LotusPaths
    {
        public const string ProductName = "LOTUS";
        public const string ServiceName = "LotusGuard";
        public const string ServiceDisplayName = "LOTUS Guard";
        public const string AppExeName = "LOTUS.exe";
        public const string GuardExeName = "LotusGuard.exe";
        public const string ProjectUrl = "https://github.com/MAXIVA11/LOTUS";

        private static string dataDirOverride;

        /// <summary>
        /// Folder that holds the policy, the block ledger and the activity log.
        /// Administrators and SYSTEM can write to it. Everyone else can only read.
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (dataDirOverride != null) return dataDirOverride;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductName);
            }
        }

        /// <summary>Points LOTUS at another data folder. Used by demo mode and by tests.</summary>
        public static void OverrideDataDir(string directory)
        {
            dataDirOverride = string.IsNullOrWhiteSpace(directory) ? null : Path.GetFullPath(directory);
        }

        /// <summary>
        /// Folder inside the data folder that holds the sealed policy store, its key
        /// and the block ledger. Only the service can write here, and only
        /// administrators can read.
        /// </summary>
        public static string SecureDir => Path.Combine(DataDir, "secure");

        public static string StoreFile => Path.Combine(SecureDir, "policy.dat");
        public static string LedgerFile => Path.Combine(SecureDir, "ledger.json");
        /// <summary>A few facts anyone may read, such as whether LOTUS is armed.</summary>
        public static string StatusFile => Path.Combine(DataDir, "status.json");
        /// <summary>Where LOTUS 1.0 kept its policy. Read once, when a newer version takes over.</summary>
        public static string LegacyPolicyFile => Path.Combine(DataDir, "policy.json");
        public static string LegacyLedgerFile => Path.Combine(DataDir, "ledger.json");
        public static string EventsFile => Path.Combine(DataDir, "events.jsonl");
        public static string OldEventsFile => Path.Combine(DataDir, "events.old.jsonl");

        public static string Version
        {
            get
            {
                Version v = typeof(LotusPaths).Assembly.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }
    }
}
