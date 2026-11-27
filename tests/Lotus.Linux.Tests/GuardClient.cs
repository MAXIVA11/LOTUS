using System.Collections.Generic;
using Lotus.Core;
using Lotus.Linux;

namespace Lotus.Tests
{
    /// <summary>
    /// The shared service loop tests talk to the loop through a "GuardClient". On
    /// Windows that is the named pipe client. Here it is the Unix socket client
    /// under the same name, so the same tests drive the Linux channel.
    /// </summary>
    internal sealed class GuardClient
    {
        private readonly UnixControlClient client;

        public GuardClient(string socketPath, bool verifyServer)
        {
            client = new UnixControlClient(socketPath);
        }

        public GuardReply Status()
        {
            return client.Status();
        }

        public GuardReply Read()
        {
            return client.Read();
        }

        public GuardReply Apply(string policyJson)
        {
            return client.Send(new Dictionary<string, object>
            {
                ["op"] = GuardProtocol.OpApply,
                ["policy"] = Json.Parse(policyJson)
            });
        }

        public GuardReply Stop(string purpose)
        {
            return client.Stop(purpose);
        }

        public GuardReply Send(Dictionary<string, object> request, int timeoutMs = 30000)
        {
            return client.Send(request, timeoutMs);
        }
    }
}
