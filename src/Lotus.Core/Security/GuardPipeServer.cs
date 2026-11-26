using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Lotus.Core
{
    /// <summary>
    /// The service's end of the control pipe. It serves one request at a time:
    /// read one line of JSON, hand it to the handler, write one line back.
    /// </summary>
    public sealed class GuardPipeServer : IDisposable
    {
        private const uint PIPE_ACCESS_DUPLEX = 0x3;
        private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x8;
        private const int RequestTimeoutMs = 10000;

        // The service may do anything. Elevated administrators may connect. Nobody
        // else may, and nobody at all over the network.
        private const string AdministratorsOnly = "D:P(D;;GA;;;NU)(A;;GA;;;SY)(A;;GRGW;;;BA)";

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            public int InheritHandle;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
            uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout,
            ref SECURITY_ATTRIBUTES security);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateNamedPipeW")]
        private static extern SafePipeHandle CreateNamedPipeDefaultW(string name, uint openMode, uint pipeMode,
            uint maxInstances, uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr security);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision,
            out IntPtr descriptor, out int size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private readonly string pipeName;
        private readonly Func<Dictionary<string, object>, Dictionary<string, object>> handler;
        private readonly Action<string> onProblem;
        private readonly bool administratorsOnly;
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private Thread thread;
        private string lastProblem;

        /// <param name="handler">Turns a request into an answer. Called on the server thread.</param>
        /// <param name="onProblem">Told when the pipe cannot be created, once per distinct problem.</param>
        /// <param name="administratorsOnly">False only in tests, which do not run elevated.</param>
        public GuardPipeServer(string pipeName,
            Func<Dictionary<string, object>, Dictionary<string, object>> handler, Action<string> onProblem = null,
            bool administratorsOnly = true)
        {
            this.pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
            this.onProblem = onProblem ?? (_ => { });
            this.administratorsOnly = administratorsOnly;
        }

        public void Start()
        {
            if (thread != null) return;
            stop.Reset();
            thread = new Thread(Run) { IsBackground = true, Name = "LotusGuard pipe" };
            thread.Start();
        }

        private NamedPipeServerStream Create()
        {
            string path = @"\\.\pipe\" + pipeName;
            // FIRST_PIPE_INSTANCE makes this fail if anyone else already created a pipe
            // with this name. Without it the service could end up sharing a pipe that
            // a hostile program set up first.
            const uint open = PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED;
            SafePipeHandle handle;
            if (administratorsOnly)
            {
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(AdministratorsOnly, 1,
                        out IntPtr descriptor, out int _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                try
                {
                    var attributes = new SECURITY_ATTRIBUTES
                    {
                        Length = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                        SecurityDescriptor = descriptor,
                        InheritHandle = 0
                    };
                    handle = CreateNamedPipeW(path, open, PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0,
                        ref attributes);
                }
                finally
                {
                    LocalFree(descriptor);
                }
            }
            else
            {
                handle = CreateNamedPipeDefaultW(path, open, PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0,
                    IntPtr.Zero);
            }
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }

        private void Run()
        {
            NamedPipeServerStream pipe = null;
            while (!stop.WaitOne(0))
            {
                if (pipe == null)
                {
                    try
                    {
                        pipe = Create();
                        lastProblem = null;
                    }
                    catch (Win32Exception ex)
                    {
                        string problem = ex.NativeErrorCode == 5 || ex.NativeErrorCode == 231
                            ? "Another program already holds the LOTUS control channel, so changes to the policy " +
                              "cannot be accepted until it lets go."
                            : "The LOTUS control channel could not be opened. " + ex.Message;
                        if (problem != lastProblem)
                        {
                            lastProblem = problem;
                            onProblem(problem);
                        }
                        if (stop.WaitOne(3000)) break;
                        continue;
                    }
                }

                try
                {
                    IAsyncResult waiting = pipe.BeginWaitForConnection(null, null);
                    if (WaitHandle.WaitAny(new[] { stop, waiting.AsyncWaitHandle }) == 0) break;
                    pipe.EndWaitForConnection(waiting);
                    Serve(pipe);
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException ||
                                           ex is InvalidOperationException)
                {
                    // A client that hung up early. Nothing to do but take the next one.
                }

                try
                {
                    if (pipe.IsConnected) pipe.Disconnect();
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException ||
                                           ex is InvalidOperationException)
                {
                    pipe.Dispose();
                    pipe = null;
                }
            }
            pipe?.Dispose();
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            Dictionary<string, object> answer;
            try
            {
                string line = ReadLine(pipe);
                Dictionary<string, object> request = Json.AsObject(Json.Parse(line));
                answer = request == null
                    ? Refuse(GuardProtocol.ErrBadRequest, "The request is not a JSON object.")
                    : handler(request);
            }
            catch (FormatException)
            {
                answer = Refuse(GuardProtocol.ErrBadRequest, "The request could not be read.");
            }
            catch (TimeoutException)
            {
                return;
            }

            if (answer == null) answer = Refuse(GuardProtocol.ErrFailed, "The request was not handled.");
            byte[] bytes = Encoding.UTF8.GetBytes(Json.Write(answer, false) + "\n");
            var write = pipe.WriteAsync(bytes, 0, bytes.Length);
            if (!write.Wait(RequestTimeoutMs)) return;
            try
            {
                pipe.WaitForPipeDrain();
            }
            catch (IOException)
            {
            }
        }

        private static Dictionary<string, object> Refuse(string code, string message)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["code"] = code, ["message"] = message };
        }

        private string ReadLine(Stream stream)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(RequestTimeoutMs);
            while (true)
            {
                var read = stream.ReadAsync(chunk, 0, chunk.Length);
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0 || !read.Wait(left)) throw new TimeoutException();
                int n = read.Result;
                if (n <= 0) throw new IOException("The client hung up.");
                int newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
                if (newline >= 0)
                {
                    buffer.Write(chunk, 0, newline);
                    break;
                }
                buffer.Write(chunk, 0, n);
                if (buffer.Length > GuardProtocol.MaxMessageBytes) throw new FormatException("The request is too long.");
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        public void Dispose()
        {
            stop.Set();
            Thread t = thread;
            thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(5));
        }
    }
}
