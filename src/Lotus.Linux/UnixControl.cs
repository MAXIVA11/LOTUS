using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Lotus.Core;

namespace Lotus.Linux
{
    /// <summary>
    /// The control channel on Linux: a Unix socket that only root can open. It
    /// speaks the same language as the named pipe on Windows, one line of JSON
    /// each way.
    /// </summary>
    public sealed class UnixControlServer : IControlServer
    {
        public const string DefaultPath = "/run/lotus/control.sock";
        private const int SOL_SOCKET = 1;
        private const int SO_PEERCRED = 17;

        private readonly string path;
        private readonly Func<Dictionary<string, object>, Dictionary<string, object>> handler;
        private readonly Action<string> problem;
        private readonly bool rootOnly;
        private Socket listener;
        private Thread thread;
        private volatile bool stopping;

        /// <param name="rootOnly">Refuse callers that are not root. Off in tests, which do not run as root.</param>
        public UnixControlServer(string path, Func<Dictionary<string, object>, Dictionary<string, object>> handler,
            Action<string> problem, bool rootOnly = true)
        {
            this.path = path ?? throw new ArgumentNullException(nameof(path));
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
            this.problem = problem ?? (_ => { });
            this.rootOnly = rootOnly;
        }

        public void Start()
        {
            if (thread != null) return;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // A socket file left by an earlier run would keep the address taken.
            if (File.Exists(path)) File.Delete(path);

            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            if (rootOnly && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            listener.Listen(8);

            thread = new Thread(Accept) { IsBackground = true, Name = "LOTUS control" };
            thread.Start();
        }

        private void Accept()
        {
            while (!stopping)
            {
                Socket client;
                try
                {
                    client = listener.Accept();
                }
                catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException)
                {
                    if (stopping) return;
                    Thread.Sleep(100);
                    continue;
                }
                using (client)
                {
                    try
                    {
                        Serve(client);
                    }
                    catch (Exception)
                    {
                        // One bad caller does not take the channel down.
                    }
                }
            }
        }

        private void Serve(Socket client)
        {
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            if (rootOnly && !CallerIsRoot(client))
            {
                problem("Something that is not running as root tried to use the LOTUS control channel.");
                return;
            }

            string line = UnixControl.ReadLine(client);
            Dictionary<string, object> request = Json.AsObject(Json.Parse(line));
            if (request == null) return;
            Dictionary<string, object> reply = handler(request);
            byte[] payload = Encoding.UTF8.GetBytes(Json.Write(reply, false) + "\n");
            client.Send(payload);
        }

        /// <summary>The kernel says who is on the other end. A caller cannot fake this.</summary>
        private static bool CallerIsRoot(Socket client)
        {
            if (OperatingSystem.IsWindows()) return true;
            try
            {
                // struct ucred: pid, uid, gid, each four bytes.
                var credentials = new byte[12];
                int length = client.GetRawSocketOption(SOL_SOCKET, SO_PEERCRED, credentials);
                return length >= 8 && BitConverter.ToUInt32(credentials, 4) == 0;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            stopping = true;
            try
            {
                listener?.Dispose();
            }
            catch (SocketException)
            {
            }
            Thread t = thread;
            thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(3));
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Talks to the LOTUS daemon over its socket.</summary>
    public sealed class UnixControlClient
    {
        private readonly string path;

        public UnixControlClient(string path = UnixControlServer.DefaultPath)
        {
            this.path = path;
        }

        public GuardReply Status()
        {
            return Send(new Dictionary<string, object> { ["op"] = GuardProtocol.OpStatus });
        }

        public GuardReply Read()
        {
            return Send(new Dictionary<string, object> { ["op"] = GuardProtocol.OpRead });
        }

        public GuardReply Apply(Policy policy)
        {
            return Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpApply,
                ["policy"] = Json.Parse(policy.ToJson())
            });
        }

        public GuardReply Stop(string purpose)
        {
            return Send(new Dictionary<string, object> { ["op"] = GuardProtocol.OpStop, ["purpose"] = purpose }, 120000);
        }

        public GuardReply Send(Dictionary<string, object> request, int timeoutMs = 30000)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                {
                    socket.ReceiveTimeout = timeoutMs;
                    socket.SendTimeout = 10000;
                    socket.Connect(new UnixDomainSocketEndPoint(path));
                    socket.Send(Encoding.UTF8.GetBytes(Json.Write(request, false) + "\n"));
                    Dictionary<string, object> answer = Json.AsObject(Json.Parse(UnixControl.ReadLine(socket)));
                    if (answer == null) throw new GuardUnavailableException("The LOTUS daemon sent an answer that makes no sense.");
                    return new GuardReply
                    {
                        Ok = Json.GetBool(answer, "ok", false),
                        Code = Json.GetString(answer, "code", ""),
                        Message = Json.GetString(answer, "message", ""),
                        Data = answer
                    };
                }
            }
            catch (SocketException ex)
            {
                throw new GuardUnavailableException(
                    ex.SocketErrorCode == SocketError.AccessDenied
                        ? "Only root can talk to the LOTUS daemon. Run this with sudo."
                        : "The LOTUS daemon is not answering. Is the service running?", ex);
            }
            catch (IOException ex)
            {
                throw new GuardUnavailableException("The connection to the LOTUS daemon broke off. " + ex.Message, ex);
            }
            catch (FormatException ex)
            {
                throw new GuardUnavailableException("The LOTUS daemon sent an answer that could not be read.", ex);
            }
        }
    }

    internal static class UnixControl
    {
        /// <summary>Reads up to the first line break, and no more than a message may be long.</summary>
        public static string ReadLine(Socket socket)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                int n = socket.Receive(chunk);
                if (n <= 0) break;
                int newline = Array.IndexOf(chunk, (byte)'\n', 0, n);
                if (newline >= 0)
                {
                    buffer.Write(chunk, 0, newline);
                    break;
                }
                buffer.Write(chunk, 0, n);
                if (buffer.Length > GuardProtocol.MaxMessageBytes) throw new IOException("The message is too long.");
            }
            if (buffer.Length == 0) throw new IOException("The other side closed the connection without a message.");
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}
