using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>Names used on the control pipe between the app, setup and the service.</summary>
    public static class GuardProtocol
    {
        public const string PipeName = "LotusGuard.Control";
        /// <summary>The pipe of a guard started by hand with --console --dev. Never used by the installed product.</summary>
        public const string DevPipeName = "LotusGuard.Dev";
        public const string ServiceName = "LotusGuard";
        public const int MaxMessageBytes = 512 * 1024;

        public const string OpStatus = "status";
        /// <summary>The policy in force and the list of blocked devices.</summary>
        public const string OpRead = "read";
        public const string OpApply = "apply";
        public const string OpStop = "stop";

        /// <summary>Stop so that a newer version can be installed. The policy stays.</summary>
        public const string PurposeUpdate = "update";
        /// <summary>Release every device, delete the data and stop, because LOTUS is being removed.</summary>
        public const string PurposeUninstall = "uninstall";

        /// <summary>LOTUS 1.1 answers this when its password is set. Later versions have no password.</summary>
        public const string ErrPasswordNeeded = "password_needed";
        public const string ErrLockdown = "lockdown";
        public const string ErrBadRequest = "bad_request";
        public const string ErrFailed = "failed";
    }

    /// <summary>What the service answered.</summary>
    public sealed class GuardReply
    {
        public bool Ok;
        /// <summary>One of the GuardProtocol.Err codes, or empty.</summary>
        public string Code = "";
        /// <summary>A sentence for the person at the screen.</summary>
        public string Message = "";
        public Dictionary<string, object> Data = new Dictionary<string, object>();

        public bool Armed => Json.GetBool(Data, "armed", false);
        public bool Lockdown => Json.GetBool(Data, "lockdown", false);
        public string Version => Json.GetString(Data, "version", "");

        public static GuardReply Fail(string code, string message)
        {
            return new GuardReply { Ok = false, Code = code, Message = message };
        }
    }

    /// <summary>The service could not be reached on its control pipe.</summary>
    public sealed class GuardUnavailableException : Exception
    {
        public GuardUnavailableException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }
}
