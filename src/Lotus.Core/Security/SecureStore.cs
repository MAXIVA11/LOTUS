using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lotus.Core
{
    /// <summary>Everything LOTUS has to keep safe: the policy and the password record.</summary>
    public sealed class StoreContent
    {
        public Policy Policy = new Policy();
        /// <summary>Null until a password has been set.</summary>
        public PasswordRecord Password;
        /// <summary>Goes up by one with every save.</summary>
        public long Serial;

        public StoreContent Clone()
        {
            return new StoreContent { Policy = Policy.Clone(), Password = Password, Serial = Serial };
        }
    }

    public enum StoreState
    {
        /// <summary>Nothing has ever been saved. A new installation.</summary>
        Fresh,
        /// <summary>The store was read and its seal is intact.</summary>
        Ok,
        /// <summary>The store was damaged and the last good copy was used instead.</summary>
        Restored,
        /// <summary>A policy file from LOTUS 1.0 was found and taken over.</summary>
        Legacy,
        /// <summary>The store exists but neither it nor its copy can be trusted.</summary>
        Tampered
    }

    public sealed class StoreLoad
    {
        public StoreState State;
        public StoreContent Content = new StoreContent();
        /// <summary>What was wrong, for the activity log. Null when nothing was.</summary>
        public string Problem;
    }

    /// <summary>
    /// The sealed policy store. The policy is still JSON, but it carries a keyed
    /// checksum made with a key only the service can read. Anyone who edits the
    /// file without that key breaks the seal, and the service then ignores the edit,
    /// puts the file back and writes down that someone tried.
    /// </summary>
    public sealed class SecureStore
    {
        private const string Header = "LOTUS-STORE-1";
        private static readonly byte[] KeyEntropy = Encoding.ASCII.GetBytes("LOTUS seal key v1");
        private const int MaxStoreBytes = 4 * 1024 * 1024;

        private readonly string directory;
        private readonly string legacyPolicyPath;
        private byte[] key;
        private byte[] lastWritten;

        public SecureStore(string secureDirectory, string legacyPolicyPath = null)
        {
            directory = secureDirectory ?? throw new ArgumentNullException(nameof(secureDirectory));
            this.legacyPolicyPath = legacyPolicyPath;
        }

        public string StorePath => Path.Combine(directory, "policy.dat");
        public string BackupPath => StorePath + ".bak";
        public string PendingPath => StorePath + ".tmp";
        public string KeyPath => Path.Combine(directory, "seal.key");

        /// <summary>
        /// The permissions the key file is created with. Left empty in tests. The
        /// service sets it so that the key is never readable by anyone else, not
        /// even for the instant it takes to write it.
        /// </summary>
        public Func<System.Security.AccessControl.FileSecurity> KeyFileSecurity;

        /// <summary>True once a seal key is loaded, which means saves can be made.</summary>
        public bool HasKey => key != null;

        // ---------- load ----------

        public StoreLoad Load()
        {
            var result = new StoreLoad();
            bool keyOnDisk = File.Exists(KeyPath);
            key = ReadKey(out string keyProblem);

            bool anyStore = File.Exists(StorePath) || File.Exists(BackupPath) || File.Exists(PendingPath);
            if (!anyStore)
            {
                if (keyOnDisk)
                {
                    result.State = StoreState.Tampered;
                    result.Problem = "The policy store is missing although LOTUS had already saved one.";
                    return result;
                }
                if (legacyPolicyPath != null &&
                    (File.Exists(legacyPolicyPath) || File.Exists(PolicyStore.BackupPath(legacyPolicyPath))))
                {
                    PolicyLoadResult legacy = PolicyStore.Load(legacyPolicyPath);
                    result.State = StoreState.Legacy;
                    result.Content.Policy = legacy.Policy;
                    result.Problem = legacy.Error;
                    return result;
                }
                result.State = StoreState.Fresh;
                return result;
            }

            if (key == null)
            {
                result.State = StoreState.Tampered;
                result.Problem = "The seal key is missing or unreadable." + (keyProblem == null ? "" : " " + keyProblem);
                return result;
            }

            var problems = new List<string>();
            // A save writes the new file under a temporary name first. If LOTUS was
            // stopped right then, the finished temporary file is the newest good state.
            foreach (string candidate in new[] { StorePath, PendingPath, BackupPath })
            {
                if (!File.Exists(candidate)) continue;
                StoreContent content = TryRead(candidate, out string problem);
                if (content == null)
                {
                    problems.Add(Path.GetFileName(candidate) + ": " + problem);
                    continue;
                }
                result.Content = content;
                result.State = candidate == StorePath ? StoreState.Ok : StoreState.Restored;
                if (problems.Count > 0) result.Problem = string.Join(" ", problems);
                else if (candidate != StorePath) result.Problem = "The policy store was missing.";
                return result;
            }

            result.State = StoreState.Tampered;
            result.Problem = string.Join(" ", problems);
            return result;
        }

        private StoreContent TryRead(string path, out string problem)
        {
            problem = null;
            try
            {
                byte[] bytes = ReadAll(path);
                if (!SplitSealed(bytes, out byte[] mac, out byte[] payload))
                {
                    problem = "it is not a sealed LOTUS store.";
                    return null;
                }
                using (var hmac = new HMACSHA256(key))
                {
                    if (!Secrets.FixedTimeEquals(hmac.ComputeHash(payload), mac))
                    {
                        problem = "its seal is broken, so it was changed outside LOTUS.";
                        return null;
                    }
                }
                StoreContent content = ParsePayload(Encoding.UTF8.GetString(payload));
                if (path == StorePath) lastWritten = bytes;
                return content;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                problem = ex.Message;
                return null;
            }
        }

        private static byte[] ReadAll(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxStoreBytes) throw new FormatException("The file is far too large.");
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != bytes.Length) Array.Resize(ref bytes, read);
                return bytes;
            }
        }

        /// <summary>Splits a sealed file into its checksum and its payload. No key is needed for this.</summary>
        private static bool SplitSealed(byte[] bytes, out byte[] mac, out byte[] payload)
        {
            mac = payload = null;
            int firstBreak = Array.IndexOf(bytes, (byte)'\n');
            if (firstBreak < 0) return false;
            int secondBreak = Array.IndexOf(bytes, (byte)'\n', firstBreak + 1);
            if (secondBreak < 0) return false;
            string header = Encoding.ASCII.GetString(bytes, 0, firstBreak).Trim();
            if (header != Header) return false;
            try
            {
                mac = Convert.FromBase64String(
                    Encoding.ASCII.GetString(bytes, firstBreak + 1, secondBreak - firstBreak - 1).Trim());
            }
            catch (FormatException)
            {
                return false;
            }
            payload = new byte[bytes.Length - secondBreak - 1];
            Buffer.BlockCopy(bytes, secondBreak + 1, payload, 0, payload.Length);
            return mac.Length == 32;
        }

        private static StoreContent ParsePayload(string text)
        {
            Dictionary<string, object> root = Json.AsObject(Json.Parse(text));
            if (root == null) throw new FormatException("The store must contain a JSON object.");
            var content = new StoreContent();
            Dictionary<string, object> policy = Json.AsObject(root.TryGetValue("policy", out object p) ? p : null);
            if (policy == null) throw new FormatException("The store holds no policy.");
            content.Policy = Policy.FromJson(Json.Write(policy, false));
            content.Password = PasswordRecord.FromJsonObject(
                Json.AsObject(root.TryGetValue("password", out object a) ? a : null));
            if (root.TryGetValue("password", out object raw) && raw != null && content.Password == null)
            {
                throw new FormatException("The password record is damaged.");
            }
            if (root.TryGetValue("serial", out object s) && s is double serial && serial >= 0 && serial < 9e15)
            {
                content.Serial = (long)serial;
            }
            return content;
        }

        /// <summary>
        /// Reads the content of a store file without checking its seal. The app and
        /// the uninstaller use this to show the policy and to check a password when
        /// the service is not running. Returns null when the file cannot be read.
        /// </summary>
        public static StoreContent ReadUnverified(string storePath)
        {
            foreach (string candidate in new[] { storePath, storePath + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    if (!SplitSealed(ReadAll(candidate), out byte[] _, out byte[] payload)) continue;
                    return ParsePayload(Encoding.UTF8.GetString(payload));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                           ex is FormatException)
                {
                }
            }
            return null;
        }

        // ---------- save ----------

        public void Save(StoreContent content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            Directory.CreateDirectory(directory);
            if (key == null) key = CreateKey();

            content.Serial++;
            var root = new Dictionary<string, object>
            {
                ["serial"] = (double)content.Serial,
                ["policy"] = Json.Parse(content.Policy.ToJson()),
                ["password"] = content.Password?.ToJsonObject()
            };
            byte[] payload = Encoding.UTF8.GetBytes(Json.Write(root));
            byte[] mac;
            using (var hmac = new HMACSHA256(key)) mac = hmac.ComputeHash(payload);

            byte[] head = Encoding.ASCII.GetBytes(Header + "\n" + Convert.ToBase64String(mac) + "\n");
            var bytes = new byte[head.Length + payload.Length];
            Buffer.BlockCopy(head, 0, bytes, 0, head.Length);
            Buffer.BlockCopy(payload, 0, bytes, head.Length, payload.Length);

            WriteFile(PendingPath, bytes);
            EnsureKeyOnDisk();
            if (File.Exists(StorePath))
            {
                try
                {
                    File.Replace(PendingPath, StorePath, BackupPath, true);
                }
                catch (IOException)
                {
                    File.Copy(StorePath, BackupPath, true);
                    File.Delete(StorePath);
                    File.Move(PendingPath, StorePath);
                }
            }
            else
            {
                File.Move(PendingPath, StorePath);
            }
            lastWritten = bytes;
        }

        private static void WriteFile(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>
        /// True when the store on disk is exactly what LOTUS wrote last. False means
        /// someone else touched it, or removed it.
        /// </summary>
        public bool IsUntouched(out string problem)
        {
            problem = null;
            if (lastWritten == null) return true; // nothing has been written by this process yet
            try
            {
                if (!File.Exists(StorePath))
                {
                    problem = "The policy store was deleted.";
                    return false;
                }
                if (!Secrets.FixedTimeEquals(ReadAll(StorePath), lastWritten))
                {
                    problem = "The policy store was changed outside LOTUS.";
                    return false;
                }
                if (!File.Exists(KeyPath))
                {
                    problem = "The seal key was deleted.";
                    return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is FormatException)
            {
                problem = "The policy store could not be read. " + ex.Message;
                return false;
            }
        }

        // ---------- the seal key ----------

        private byte[] ReadKey(out string problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(KeyPath)) return null;
                byte[] sealedKey = ReadAll(KeyPath);
                byte[] plain = ProtectedData.Unprotect(sealedKey, KeyEntropy, DataProtectionScope.LocalMachine);
                if (plain.Length != 32)
                {
                    problem = "The seal key has the wrong length.";
                    return null;
                }
                return plain;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is CryptographicException || ex is FormatException)
            {
                problem = ex.Message;
                return null;
            }
        }

        private static byte[] CreateKey()
        {
            return Secrets.RandomBytes(32);
        }

        private void EnsureKeyOnDisk()
        {
            if (File.Exists(KeyPath))
            {
                byte[] onDisk = ReadKey(out string _);
                if (onDisk != null && Secrets.FixedTimeEquals(onDisk, key)) return;
            }
            byte[] sealedKey = ProtectedData.Protect(key, KeyEntropy, DataProtectionScope.LocalMachine);
            string temp = KeyPath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            if (KeyFileSecurity != null)
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew,
                           System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096,
                           FileOptions.None, KeyFileSecurity()))
                {
                    stream.Write(sealedKey, 0, sealedKey.Length);
                    stream.Flush(true);
                }
            }
            else
            {
                WriteFile(temp, sealedKey);
            }
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
            File.Move(temp, KeyPath);
        }

        /// <summary>Forgets the files. Used when LOTUS is uninstalled.</summary>
        public void Erase()
        {
            foreach (string path in new[] { StorePath, BackupPath, PendingPath, KeyPath, KeyPath + ".tmp" })
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
            key = null;
            lastWritten = null;
        }
    }
}
