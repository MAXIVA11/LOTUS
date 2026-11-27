using System.Security.Cryptography;

namespace Lotus.Core
{
    public static class Secrets
    {
        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return bytes;
        }

        /// <summary>Compares two byte strings in a time that does not depend on where they differ.</summary>
        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
    }
}
