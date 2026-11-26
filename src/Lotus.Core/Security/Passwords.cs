using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Lotus.Core
{
    /// <summary>
    /// PBKDF2 with HMAC-SHA256. The .NET Framework version LOTUS targets only
    /// ships the SHA-1 variant, so the few lines of the standard are written out here.
    /// </summary>
    public static class Pbkdf2
    {
        public static byte[] Sha256(byte[] password, byte[] salt, int iterations, int length)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            if (salt == null) throw new ArgumentNullException(nameof(salt));
            if (iterations < 1) throw new ArgumentOutOfRangeException(nameof(iterations));
            if (length < 1 || length > 1024) throw new ArgumentOutOfRangeException(nameof(length));

            var output = new byte[length];
            using (var hmac = new HMACSHA256(password))
            {
                int blocks = (length + 31) / 32;
                var saltBlock = new byte[salt.Length + 4];
                Buffer.BlockCopy(salt, 0, saltBlock, 0, salt.Length);
                for (int block = 1; block <= blocks; block++)
                {
                    saltBlock[salt.Length] = (byte)(block >> 24);
                    saltBlock[salt.Length + 1] = (byte)(block >> 16);
                    saltBlock[salt.Length + 2] = (byte)(block >> 8);
                    saltBlock[salt.Length + 3] = (byte)block;

                    byte[] u = hmac.ComputeHash(saltBlock);
                    var t = (byte[])u.Clone();
                    for (int i = 1; i < iterations; i++)
                    {
                        u = hmac.ComputeHash(u);
                        for (int j = 0; j < t.Length; j++) t[j] ^= u[j];
                    }
                    int offset = (block - 1) * 32;
                    Buffer.BlockCopy(t, 0, output, offset, Math.Min(32, length - offset));
                }
            }
            return output;
        }
    }

    public static class Secrets
    {
        public static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var random = new RNGCryptoServiceProvider()) random.GetBytes(bytes);
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

    /// <summary>
    /// What LOTUS keeps of its password: a salted, slow hash. The password itself
    /// is never stored.
    /// </summary>
    public sealed class PasswordRecord
    {
        public const int MinLength = 8;
        public const int MaxLength = 256;
        public const int DefaultIterations = 210000;
        /// <summary>A stored record may ask for at most this much work, so a planted file cannot stall the service.</summary>
        public const int MaxIterations = 2000000;

        public byte[] Salt = new byte[0];
        public byte[] Hash = new byte[0];
        public int Iterations = DefaultIterations;

        /// <summary>Why a new password is not acceptable, or null when it is.</summary>
        public static string Problem(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            {
                return "The password needs at least " + MinLength + " characters.";
            }
            if (password.Length > MaxLength) return "The password is too long.";
            return null;
        }

        public static PasswordRecord Create(string password, int iterations = DefaultIterations)
        {
            string problem = Problem(password);
            if (problem != null) throw new ArgumentException(problem, nameof(password));
            var record = new PasswordRecord { Salt = Secrets.RandomBytes(16), Iterations = iterations };
            record.Hash = Pbkdf2.Sha256(Encoding.UTF8.GetBytes(password), record.Salt, iterations, 32);
            return record;
        }

        public bool Verify(string password)
        {
            if (password == null || password.Length > MaxLength) return false;
            if (Salt.Length < 8 || Hash.Length < 16 || Iterations < 1 || Iterations > MaxIterations) return false;
            byte[] candidate = Pbkdf2.Sha256(Encoding.UTF8.GetBytes(password), Salt, Iterations, Hash.Length);
            return Secrets.FixedTimeEquals(candidate, Hash);
        }

        public Dictionary<string, object> ToJsonObject()
        {
            return new Dictionary<string, object>
            {
                ["salt"] = Convert.ToBase64String(Salt),
                ["hash"] = Convert.ToBase64String(Hash),
                // The JSON reader hands numbers back as double, so store one.
                ["iterations"] = (double)Iterations
            };
        }

        /// <summary>Reads a record. Returns null when the object is missing or malformed.</summary>
        public static PasswordRecord FromJsonObject(Dictionary<string, object> obj)
        {
            if (obj == null) return null;
            try
            {
                var record = new PasswordRecord
                {
                    Salt = Convert.FromBase64String(Json.GetString(obj, "salt", "")),
                    Hash = Convert.FromBase64String(Json.GetString(obj, "hash", "")),
                    Iterations = Json.GetInt(obj, "iterations", 0)
                };
                if (record.Salt.Length < 8 || record.Hash.Length < 16 || record.Iterations < 1 ||
                    record.Iterations > MaxIterations)
                {
                    return null;
                }
                return record;
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
