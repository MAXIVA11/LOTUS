using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Lotus.Core
{
    /// <summary>Names used on the control pipe between the app, setup and the service.</summary>
    public static class GuardProtocol
    {
        public const string PipeName = "LotusGuard.Control";
        public const string ServiceName = "LotusGuard";
        public const int MaxMessageBytes = 512 * 1024;

        public const string OpStatus = "status";
        public const string OpVerify = "verify";
        public const string OpApply = "apply";
        public const string OpSetPassword = "set_password";
        public const string OpStop = "stop";

        /// <summary>Stop so that a newer version can be installed. The policy stays.</summary>
        public const string PurposeUpdate = "update";
        /// <summary>Release every device, delete the data and stop, because LOTUS is being removed.</summary>
        public const string PurposeUninstall = "uninstall";

        public const string ErrWrongPassword = "wrong_password";
        public const string ErrPasswordNeeded = "password_needed";
        public const string ErrNewPasswordNeeded = "new_password_needed";
        public const string ErrWeakPassword = "weak_password";
        public const string ErrLockedOut = "locked_out";
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

        public bool HasPassword => Json.GetBool(Data, "hasPassword", false);
        public bool Armed => Json.GetBool(Data, "armed", false);
        public bool Lockdown => Json.GetBool(Data, "lockdown", false);
        public int RetryAfterSeconds => Json.GetInt(Data, "retryAfter", 0);

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

    /// <summary>
    /// Talks to the LOTUS Guard service. Every change to the policy goes this way:
    /// the service checks the password and writes the policy itself, so there is no
    /// file an administrator's editor could change.
    /// </summary>
    public sealed class GuardClient
    {
        private readonly string pipeName;
        private readonly bool verifyServer;

        /// <param name="verifyServer">
        /// Check that the process behind the pipe really is the LotusGuard service
        /// before sending it anything. Only tests turn this off.
        /// </param>
        public GuardClient(string pipeName = GuardProtocol.PipeName, bool verifyServer = true)
        {
            this.pipeName = pipeName;
            this.verifyServer = verifyServer;
        }

        public GuardReply Status()
        {
            return Send(new Dictionary<string, object> { ["op"] = GuardProtocol.OpStatus });
        }

        public GuardReply Verify(string password)
        {
            return Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpVerify,
                ["password"] = password ?? ""
            });
        }

        /// <param name="policyJson">The policy as JSON text.</param>
        /// <param name="newPassword">Sets the password in the same step. Needed the first time LOTUS is armed.</param>
        public GuardReply Apply(string policyJson, string password, string newPassword = null)
        {
            var request = new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpApply,
                ["password"] = password ?? "",
                ["policy"] = Json.Parse(policyJson)
            };
            if (!string.IsNullOrEmpty(newPassword)) request["newPassword"] = newPassword;
            return Send(request);
        }

        public GuardReply SetPassword(string password, string newPassword)
        {
            return Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpSetPassword,
                ["password"] = password ?? "",
                ["newPassword"] = newPassword ?? ""
            });
        }

        public GuardReply Stop(string password, string purpose)
        {
            return Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpStop,
                ["password"] = password ?? "",
                ["purpose"] = purpose
            }, 120000);
        }

        public GuardReply Send(Dictionary<string, object> request, int timeoutMs = 30000)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    try
                    {
                        pipe.Connect(3000);
                    }
                    catch (TimeoutException ex)
                    {
                        throw new GuardUnavailableException("LOTUS Guard is not answering. Is the service running?", ex);
                    }

                    if (verifyServer && !ServerIsTheService(pipe))
                    {
                        throw new GuardUnavailableException(
                            "Something other than the LOTUS Guard service answered on its channel. " +
                            "Nothing was sent to it.");
                    }

                    pipe.ReadMode = PipeTransmissionMode.Byte;
                    byte[] payload = Encoding.UTF8.GetBytes(Json.Write(request, false) + "\n");
                    pipe.Write(payload, 0, payload.Length);
                    pipe.Flush();

                    string line = ReadLine(pipe, timeoutMs);
                    Dictionary<string, object> answer = Json.AsObject(Json.Parse(line));
                    if (answer == null) throw new GuardUnavailableException("LOTUS Guard sent an answer that makes no sense.");
                    return new GuardReply
                    {
                        Ok = Json.GetBool(answer, "ok", false),
                        Code = Json.GetString(answer, "code", ""),
                        Message = Json.GetString(answer, "message", ""),
                        Data = answer
                    };
                }
            }
            catch (IOException ex)
            {
                throw new GuardUnavailableException("The connection to LOTUS Guard broke off. " + ex.Message, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new GuardUnavailableException(
                    "Windows refused the connection to LOTUS Guard. LOTUS has to run as administrator.", ex);
            }
            catch (FormatException ex)
            {
                throw new GuardUnavailableException("LOTUS Guard sent an answer that could not be read.", ex);
            }
        }

        private static string ReadLine(Stream stream, int timeoutMs)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                // A pipe read has no timeout of its own, so the wait is done on the task.
                var read = stream.ReadAsync(chunk, 0, chunk.Length);
                int left = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (!read.Wait(left)) throw new IOException("LOTUS Guard took too long to answer.");
                int n = read.Result;
                if (n <= 0) break;
                int newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
                if (newline >= 0)
                {
                    buffer.Write(chunk, 0, newline);
                    break;
                }
                buffer.Write(chunk, 0, n);
                if (buffer.Length > GuardProtocol.MaxMessageBytes) throw new IOException("The answer is too long.");
            }
            if (buffer.Length == 0) throw new IOException("LOTUS Guard closed the connection without answering.");
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        // ---------- who is on the other end ----------

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, byte[] buffer, int size,
            out int needed);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        /// <summary>
        /// The process ID Windows itself reports for the running LotusGuard service,
        /// or 0 when the service is not running. A program without administrator
        /// rights cannot fake this answer.
        /// </summary>
        public static uint ServiceProcessId()
        {
            const uint SC_MANAGER_CONNECT = 0x1, SERVICE_QUERY_STATUS = 0x4;
            IntPtr manager = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (manager == IntPtr.Zero) return 0;
            try
            {
                IntPtr service = OpenServiceW(manager, GuardProtocol.ServiceName, SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero) return 0;
                try
                {
                    // SERVICE_STATUS_PROCESS: nine 4-byte fields, the process ID is the eighth.
                    var buffer = new byte[36];
                    if (!QueryServiceStatusEx(service, 0, buffer, buffer.Length, out int _)) return 0;
                    return BitConverter.ToUInt32(buffer, 28);
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
            finally
            {
                CloseServiceHandle(manager);
            }
        }

        private static bool ServerIsTheService(NamedPipeClientStream pipe)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint server)) return false;
            uint service = ServiceProcessId();
            return service != 0 && server == service;
        }
    }
}
