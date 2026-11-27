using System.Security.Cryptography;
using System.Text;

namespace Lotus.Core
{
    public static class PlatformKeyWrap
    {
        public static IKeyWrap Create()
        {
            return new MachineKeyWrap();
        }
    }

    /// <summary>
    /// Windows: the key is wrapped by the data protection service with the
    /// machine's own secret, so the key file cannot be used on another PC.
    /// </summary>
    public sealed class MachineKeyWrap : IKeyWrap
    {
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("LOTUS seal key v1");

        public byte[] Wrap(byte[] key)
        {
            return ProtectedData.Protect(key, Entropy, DataProtectionScope.LocalMachine);
        }

        public byte[] Unwrap(byte[] wrapped)
        {
            return ProtectedData.Unprotect(wrapped, Entropy, DataProtectionScope.LocalMachine);
        }
    }
}
