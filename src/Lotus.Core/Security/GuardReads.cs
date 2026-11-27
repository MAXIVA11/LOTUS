using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>Takes the policy and the list of blocked devices out of an answer to GuardClient.Read.</summary>
    public static class GuardReads
    {
        /// <summary>The policy in the answer. Null when the answer holds none.</summary>
        public static Policy ReadPolicy(this GuardReply reply)
        {
            Dictionary<string, object> raw =
                Json.AsObject(reply.Data.TryGetValue("policy", out object value) ? value : null);
            return raw == null ? null : Policy.FromJson(Json.Write(raw, false));
        }

        /// <summary>The list of blocked devices in the answer. Empty when the answer holds none.</summary>
        public static Ledger ReadLedger(this GuardReply reply)
        {
            Dictionary<string, object> raw =
                Json.AsObject(reply.Data.TryGetValue("ledger", out object value) ? value : null);
            return raw == null ? new Ledger() : Ledger.FromJson(Json.Write(raw, false));
        }
    }
}
