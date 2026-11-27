using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Lotus.Core
{
    /// <summary>
    /// Talks to the LOTUS Guard service. The policy is read and changed this way
    /// and no other: the service keeps it in an encrypted store that nothing else
    /// can read or write.
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

        /// <summary>Asks for the policy in force and the list of blocked devices.</summary>
        public GuardReply Read()
        {
            return Send(new Dictionary<string, object> { ["op"] = GuardProtocol.OpRead });
        }

        /// <param name="policyJson">The policy as text, the way Policy.ToJson writes it.</param>
        public GuardReply Apply(string policyJson)
        {
            return Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpApply,
                ["policy"] = Json.Parse(policyJson)
            });
        }

        public GuardReply Stop(string purpose)
        {
            return Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpStop,
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
