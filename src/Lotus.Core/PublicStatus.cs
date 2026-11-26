using System;
using System.Collections.Generic;
using System.IO;

namespace Lotus.Core
{
    /// <summary>
    /// The few facts about LOTUS that anyone on the PC may read: whether it is
    /// armed and whether it is locked down. The notifier and the setup program use
    /// it. The policy itself, with the trusted list, is not in here.
    /// </summary>
    public sealed class PublicStatus
    {
        public bool Known;
        public bool Armed;
        public bool HasPassword;
        public bool Lockdown;
        public string Version = "";

        public static PublicStatus Read()
        {
            return Read(LotusPaths.StatusFile);
        }

        public static PublicStatus Read(string path)
        {
            var status = new PublicStatus();
            try
            {
                if (!File.Exists(path)) return status;
                if (new FileInfo(path).Length > 64 * 1024) return status;
                Dictionary<string, object> root = Json.AsObject(Json.Parse(PolicyStore.ReadShared(path)));
                if (root == null) return status;
                status.Known = true;
                status.Armed = Json.GetBool(root, "armed", false);
                status.HasPassword = Json.GetBool(root, "hasPassword", false);
                status.Lockdown = Json.GetBool(root, "lockdown", false);
                status.Version = Json.GetString(root, "version", "");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
            }
            return status;
        }
    }
}
