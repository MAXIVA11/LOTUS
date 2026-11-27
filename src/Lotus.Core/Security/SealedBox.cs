using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lotus.Core
{
    /// <summary>
    /// The container every LOTUS store file is written in: the content is
    /// encrypted with AES-256 and the whole file is authenticated with
    /// HMAC-SHA256 (encrypt, then authenticate). Without the key the file is
    /// noise, and a file that was changed in any way is refused before a single
    /// byte of it is decrypted.
    ///
    /// Layout, all numbers little-endian:
    ///   6   "LOTUS" and a zero byte
    ///   1   format version (2)
    ///   1   kind of content
    ///   8   serial number of this save
    ///   16  initialisation vector, fresh for every save
    ///   4   length of the encrypted part
    ///   n   encrypted part
    ///   32  HMAC-SHA256 over everything before it
    /// </summary>
    public static class SealedBox
    {
        public const byte KindPolicy = 1;
        public const byte KindLedger = 2;

        private const byte FormatVersion = 2;
        private const int HeaderBytes = 36;
        private const int TagBytes = 32;
        /// <summary>The content is padded to a multiple of this, so the file size says little about what is inside.</summary>
        private const int PadTo = 1024;
        private const int MaxPlainBytes = 8 * 1024 * 1024;

        private static readonly byte[] Magic = { 0x4C, 0x4F, 0x54, 0x55, 0x53, 0x00 };

        /// <summary>True when the bytes start like a LOTUS store file. Says nothing about whether it is intact.</summary>
        public static bool LooksSealed(byte[] file)
        {
            if (file == null || file.Length < HeaderBytes + TagBytes) return false;
            for (int i = 0; i < Magic.Length; i++)
            {
                if (file[i] != Magic[i]) return false;
            }
            return true;
        }

        public static byte[] Seal(byte[] masterKey, byte kind, long serial, byte[] plain)
        {
            if (masterKey == null || masterKey.Length != 32) throw new ArgumentException("The key must be 32 bytes.", nameof(masterKey));
            if (plain == null) throw new ArgumentNullException(nameof(plain));
            if (plain.Length > MaxPlainBytes) throw new ArgumentException("The content is far too large.", nameof(plain));

            // The real length first, then the content, then zeros up to the next full block.
            int framed = 4 + plain.Length;
            int padded = (framed + PadTo - 1) / PadTo * PadTo;
            var frame = new byte[padded];
            WriteInt32(frame, 0, plain.Length);
            Buffer.BlockCopy(plain, 0, frame, 4, plain.Length);

            byte[] iv = Secrets.RandomBytes(16);
            byte[] cipher;
            byte[] encryptionKey = DeriveKey(masterKey, "LOTUS store v2 encryption", kind);
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    {
                        cipher = encryptor.TransformFinalBlock(frame, 0, frame.Length);
                    }
                }
            }
            finally
            {
                Array.Clear(encryptionKey, 0, encryptionKey.Length);
                Array.Clear(frame, 0, frame.Length);
            }

            var file = new byte[HeaderBytes + cipher.Length + TagBytes];
            Buffer.BlockCopy(Magic, 0, file, 0, Magic.Length);
            file[6] = FormatVersion;
            file[7] = kind;
            WriteInt64(file, 8, serial);
            Buffer.BlockCopy(iv, 0, file, 16, 16);
            WriteInt32(file, 32, cipher.Length);
            Buffer.BlockCopy(cipher, 0, file, HeaderBytes, cipher.Length);

            byte[] tag = ComputeTag(masterKey, kind, file, HeaderBytes + cipher.Length);
            Buffer.BlockCopy(tag, 0, file, HeaderBytes + cipher.Length, TagBytes);
            return file;
        }

        /// <summary>
        /// Checks and decrypts a store file. Throws FormatException when the file is
        /// not a LOTUS store, is of another kind, or was changed after LOTUS wrote it.
        /// </summary>
        public static byte[] Open(byte[] masterKey, byte kind, byte[] file, out long serial)
        {
            serial = 0;
            if (masterKey == null || masterKey.Length != 32) throw new ArgumentException("The key must be 32 bytes.", nameof(masterKey));
            if (!LooksSealed(file)) throw new FormatException("it is not a LOTUS store.");
            if (file[6] != FormatVersion) throw new FormatException("it was written in a format this version does not know.");
            if (file[7] != kind) throw new FormatException("it is a LOTUS store of another kind.");

            int cipherLength = ReadInt32(file, 32);
            if (cipherLength <= 0 || cipherLength % 16 != 0 || cipherLength != file.Length - HeaderBytes - TagBytes)
            {
                throw new FormatException("its length does not add up.");
            }

            byte[] expected = ComputeTag(masterKey, kind, file, HeaderBytes + cipherLength);
            var found = new byte[TagBytes];
            Buffer.BlockCopy(file, HeaderBytes + cipherLength, found, 0, TagBytes);
            if (!Secrets.FixedTimeEquals(expected, found))
            {
                throw new FormatException("its seal is broken, so it was changed outside LOTUS.");
            }

            var iv = new byte[16];
            Buffer.BlockCopy(file, 16, iv, 0, 16);
            byte[] frame;
            byte[] encryptionKey = DeriveKey(masterKey, "LOTUS store v2 encryption", kind);
            try
            {
                using (Aes aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    {
                        frame = decryptor.TransformFinalBlock(file, HeaderBytes, cipherLength);
                    }
                }
            }
            catch (CryptographicException)
            {
                throw new FormatException("it could not be decrypted.");
            }
            finally
            {
                Array.Clear(encryptionKey, 0, encryptionKey.Length);
            }

            if (frame.Length < 4) throw new FormatException("its content is cut short.");
            int length = ReadInt32(frame, 0);
            if (length < 0 || length > frame.Length - 4) throw new FormatException("its content is cut short.");
            var plain = new byte[length];
            Buffer.BlockCopy(frame, 4, plain, 0, length);
            Array.Clear(frame, 0, frame.Length);
            serial = ReadInt64(file, 8);
            return plain;
        }

        private static byte[] ComputeTag(byte[] masterKey, byte kind, byte[] file, int count)
        {
            byte[] key = DeriveKey(masterKey, "LOTUS store v2 authentication", kind);
            try
            {
                using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(file, 0, count);
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        /// <summary>
        /// Separate keys for encrypting and for authenticating, and separate keys
        /// for each kind of file, all from the one key LOTUS keeps. A ledger can
        /// therefore never be passed off as a policy.
        /// </summary>
        private static byte[] DeriveKey(byte[] masterKey, string purpose, byte kind)
        {
            byte[] label = Encoding.ASCII.GetBytes(purpose);
            var info = new byte[label.Length + 1];
            Buffer.BlockCopy(label, 0, info, 0, label.Length);
            info[label.Length] = kind;
            using (var hmac = new HMACSHA256(masterKey)) return hmac.ComputeHash(info);
        }

        private static void WriteInt32(byte[] bytes, int at, int value)
        {
            bytes[at] = (byte)value;
            bytes[at + 1] = (byte)(value >> 8);
            bytes[at + 2] = (byte)(value >> 16);
            bytes[at + 3] = (byte)(value >> 24);
        }

        private static void WriteInt64(byte[] bytes, int at, long value)
        {
            WriteInt32(bytes, at, (int)value);
            WriteInt32(bytes, at + 4, (int)(value >> 32));
        }

        private static int ReadInt32(byte[] bytes, int at)
        {
            return bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16 | bytes[at + 3] << 24;
        }

        private static long ReadInt64(byte[] bytes, int at)
        {
            return (uint)ReadInt32(bytes, at) | (long)ReadInt32(bytes, at + 4) << 32;
        }
    }
}
