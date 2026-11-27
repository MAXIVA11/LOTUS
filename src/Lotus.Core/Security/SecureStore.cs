using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Lotus.Core
{
    /// <summary>What LOTUS has to keep safe: the policy.</summary>
    public sealed class StoreContent
    {
        public Policy Policy = new Policy();
        /// <summary>Goes up by one with every save.</summary>
        public long Serial;

        public StoreContent Clone()
        {
            return new StoreContent { Policy = Policy.Clone(), Serial = Serial };
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
        /// <summary>A policy from LOTUS 1.0 or 1.1 was found and taken over.</summary>
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
    /// The encrypted policy store. The policy and the list of blocked devices are
    /// kept as binary records inside encrypted, authenticated files. The key is 32
    /// random bytes that only the service account can read. Someone who finds the
    /// files sees noise, and someone who changes them breaks the seal: the service
    /// then ignores the change, puts its own copy back and writes down that
    /// someone tried.
    /// </summary>
    public sealed class SecureStore
    {
        private const string OldHeader = "LOTUS-STORE-1";
        private const int MaxStoreBytes = 12 * 1024 * 1024;

        private readonly string directory;
        private readonly string legacyPolicyPath;
        private byte[] key;
        private byte[] lastWritten;
        private bool oldFormatSeen;

        public SecureStore(string secureDirectory, string legacyPolicyPath = null)
        {
            directory = secureDirectory ?? throw new ArgumentNullException(nameof(secureDirectory));
            this.legacyPolicyPath = legacyPolicyPath;
        }

        public string StorePath => Path.Combine(directory, "policy.dat");
        public string BackupPath => StorePath + ".bak";
        public string PendingPath => StorePath + ".tmp";
        public string KeyPath => Path.Combine(directory, "seal.key");
        public string LedgerPath => Path.Combine(directory, "ledger.dat");
        /// <summary>Where LOTUS 1.1 kept the list of blocked devices, readable by administrators.</summary>
        public string OldLedgerPath => Path.Combine(directory, "ledger.json");

#if NETFRAMEWORK
        /// <summary>
        /// The permissions the key file is created with on Windows. Left empty in
        /// tests. The service sets it so that the key is never readable by anyone
        /// else, not even for the instant it takes to write it.
        /// </summary>
        public Func<System.Security.AccessControl.FileSecurity> KeyFileSecurity;
#endif

        /// <summary>
        /// Called with the path of a key file that was just written and is about
        /// to be put in place. Systems without Windows permissions use it to make
        /// the file readable by its owner only.
        /// </summary>
        public Action<string> ProtectKeyFile;

        /// <summary>
        /// Wraps and unwraps the key for the disk. On Windows this is the data
        /// protection service of the machine, so the key file is useless on any
        /// other PC. Tests and other systems put their own in.
        /// </summary>
        public IKeyWrap KeyWrap = PlatformKeyWrap.Create();

        /// <summary>True once a key is loaded, which means saves can be made.</summary>
        public bool HasKey => key != null;

        // ---------- load ----------

        public StoreLoad Load()
        {
            var result = new StoreLoad();
            bool keyOnDisk = File.Exists(KeyPath);
            key = ReadKey(out string keyProblem);
            oldFormatSeen = false;

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
                result.Problem = "The key of the policy store is missing or unreadable." +
                                 (keyProblem == null ? "" : " " + keyProblem);
                return result;
            }

            var problems = new List<string>();
            // A save writes the new file under a temporary name first. If LOTUS was
            // stopped right then, the finished temporary file is the newest good state.
            foreach (string candidate in new[] { StorePath, PendingPath, BackupPath })
            {
                if (!File.Exists(candidate)) continue;
                StoreContent content = TryRead(candidate, out string problem, out bool oldFormat);
                if (content == null)
                {
                    problems.Add(Path.GetFileName(candidate) + ": " + problem);
                    continue;
                }
                result.Content = content;
                if (oldFormat)
                {
                    // Written by LOTUS 1.1: sealed, but readable. It is rewritten
                    // in the encrypted form at the next save.
                    oldFormatSeen = true;
                    result.State = StoreState.Legacy;
                    if (problems.Count > 0) result.Problem = string.Join(" ", problems);
                    return result;
                }
                result.State = candidate == StorePath ? StoreState.Ok : StoreState.Restored;
                if (problems.Count > 0) result.Problem = string.Join(" ", problems);
                else if (candidate != StorePath) result.Problem = "The policy store was missing.";
                return result;
            }

            result.State = StoreState.Tampered;
            result.Problem = string.Join(" ", problems);
            return result;
        }

        private StoreContent TryRead(string path, out string problem, out bool oldFormat)
        {
            problem = null;
            oldFormat = false;
            try
            {
                byte[] bytes = ReadAll(path);
                if (SealedBox.LooksSealed(bytes))
                {
                    byte[] plain = SealedBox.Open(key, SealedBox.KindPolicy, bytes, out long serial);
                    StoreContent content = FromRecord(ObjectCodec.Decode(plain));
                    content.Serial = serial;
                    if (path == StorePath) lastWritten = bytes;
                    return content;
                }
                if (SplitOldFormat(bytes, out byte[] mac, out byte[] payload))
                {
                    using (var hmac = new HMACSHA256(key))
                    {
                        if (!Secrets.FixedTimeEquals(hmac.ComputeHash(payload), mac))
                        {
                            problem = "its seal is broken, so it was changed outside LOTUS.";
                            return null;
                        }
                    }
                    oldFormat = true;
                    return ParseOldPayload(Encoding.UTF8.GetString(payload));
                }
                problem = "it is not a LOTUS store.";
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException)
            {
                problem = ex.Message;
                return null;
            }
        }

        private static StoreContent FromRecord(object record)
        {
            Dictionary<string, object> root = Json.AsObject(record);
            if (root == null) throw new FormatException("The store holds no record.");
            Dictionary<string, object> policy = Json.AsObject(root.TryGetValue("policy", out object p) ? p : null);
            if (policy == null) throw new FormatException("The store holds no policy.");
            return new StoreContent { Policy = Policy.FromJson(Json.Write(policy, false)) };
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

        // ---------- the store LOTUS 1.1 wrote ----------

        /// <summary>Splits a 1.1 store into its checksum and its payload.</summary>
        private static bool SplitOldFormat(byte[] bytes, out byte[] mac, out byte[] payload)
        {
            mac = payload = null;
            int firstBreak = Array.IndexOf(bytes, (byte)'\n');
            if (firstBreak < 0) return false;
            int secondBreak = Array.IndexOf(bytes, (byte)'\n', firstBreak + 1);
            if (secondBreak < 0) return false;
            string header = Encoding.ASCII.GetString(bytes, 0, firstBreak).Trim();
            if (header != OldHeader) return false;
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

        /// <summary>Takes the policy out of a 1.1 store. The password record it also held is dropped.</summary>
        private static StoreContent ParseOldPayload(string text)
        {
            Dictionary<string, object> root = Json.AsObject(Json.Parse(text));
            if (root == null) throw new FormatException("The store holds no record.");
            Dictionary<string, object> policy = Json.AsObject(root.TryGetValue("policy", out object p) ? p : null);
            if (policy == null) throw new FormatException("The store holds no policy.");
            var content = new StoreContent { Policy = Policy.FromJson(Json.Write(policy, false)) };
            if (root.TryGetValue("serial", out object s) && s is double serial && serial >= 0 && serial < 9e15)
            {
                content.Serial = (long)serial;
            }
            return content;
        }

        // ---------- save ----------

        public void Save(StoreContent content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            Directory.CreateDirectory(directory);
            if (key == null) key = Secrets.RandomBytes(32);

            content.Serial++;
            var record = new Dictionary<string, object>
            {
                ["policy"] = Json.Parse(content.Policy.ToJson())
            };
            byte[] bytes = SealedBox.Seal(key, SealedBox.KindPolicy, content.Serial, ObjectCodec.Encode(record));

            WriteFile(PendingPath, bytes);
            EnsureKeyOnDisk();
            Replace(PendingPath, StorePath, BackupPath);
            if (oldFormatSeen)
            {
                // The copy that was just pushed aside is the readable 1.1 store.
                // Do not leave it lying around.
                File.Copy(StorePath, BackupPath, true);
                oldFormatSeen = false;
            }
            lastWritten = bytes;
        }

        private static void Replace(string pending, string target, string backup)
        {
            if (File.Exists(target))
            {
                try
                {
                    File.Replace(pending, target, backup, true);
                }
                catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException)
                {
                    File.Copy(target, backup, true);
                    File.Delete(target);
                    File.Move(pending, target);
                }
            }
            else
            {
                File.Move(pending, target);
            }
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
                    problem = "The key of the policy store was deleted.";
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

        /// <summary>
        /// Throws away a policy store that cannot be trusted, so that LOTUS starts
        /// again as on a new PC. The key and the list of blocked devices stay when
        /// they are still readable, so devices LOTUS switched off can be switched
        /// back on.
        /// </summary>
        public void ResetPolicy()
        {
            Delete(StorePath, BackupPath, PendingPath);
            lastWritten = null;
            oldFormatSeen = false;
            if (key == null) Delete(KeyPath, KeyPath + ".tmp");
        }

        // ---------- the list of blocked devices ----------

        /// <summary>
        /// Loads the ledger. A missing file is an empty ledger. error is set when a
        /// file was there but could not be trusted or read.
        /// </summary>
        public Ledger LoadLedger(out string error)
        {
            error = null;
            if (key != null)
            {
                foreach (string candidate in new[] { LedgerPath, LedgerPath + ".tmp", LedgerPath + ".bak" })
                {
                    try
                    {
                        if (!File.Exists(candidate)) continue;
                        byte[] plain = SealedBox.Open(key, SealedBox.KindLedger, ReadAll(candidate), out long _);
                        Dictionary<string, object> record = Json.AsObject(ObjectCodec.Decode(plain));
                        if (record == null) throw new FormatException("it holds no record.");
                        return Ledger.FromJson(Json.Write(record, false));
                    }
                    catch (Exception ex) when (ex is FormatException || ex is IOException ||
                                               ex is UnauthorizedAccessException)
                    {
                        error = (error == null ? "" : error + " ") + Path.GetFileName(candidate) + ": " + ex.Message;
                    }
                }
            }
            else if (File.Exists(LedgerPath) || File.Exists(LedgerPath + ".bak"))
            {
                error = "The list of blocked devices cannot be read without the key of the policy store.";
            }
            return new Ledger();
        }

        public void SaveLedger(Ledger ledger)
        {
            if (ledger == null) throw new ArgumentNullException(nameof(ledger));
            Directory.CreateDirectory(directory);
            if (key == null)
            {
                key = Secrets.RandomBytes(32);
            }
            EnsureKeyOnDisk();
            object record = Json.Parse(ledger.ToJson());
            byte[] bytes = SealedBox.Seal(key, SealedBox.KindLedger, 0, ObjectCodec.Encode(record));
            string pending = LedgerPath + ".tmp";
            WriteFile(pending, bytes);
            Replace(pending, LedgerPath, LedgerPath + ".bak");
        }

        // ---------- the key ----------

        private byte[] ReadKey(out string problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(KeyPath)) return null;
                byte[] plain = KeyWrap.Unwrap(ReadAll(KeyPath));
                if (plain == null || plain.Length != 32)
                {
                    problem = "The key has the wrong length.";
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

        private void EnsureKeyOnDisk()
        {
            if (File.Exists(KeyPath))
            {
                byte[] onDisk = ReadKey(out string _);
                if (onDisk != null && Secrets.FixedTimeEquals(onDisk, key)) return;
            }
            byte[] wrapped = KeyWrap.Wrap(key);
            string temp = KeyPath + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
            bool written = false;
#if NETFRAMEWORK
            if (KeyFileSecurity != null)
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew,
                           System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096,
                           FileOptions.None, KeyFileSecurity()))
                {
                    stream.Write(wrapped, 0, wrapped.Length);
                    stream.Flush(true);
                }
                written = true;
            }
#endif
            if (!written)
            {
                if (ProtectKeyFile != null)
                {
                    // Empty first, protected next, filled last: the key never sits in a file others could open.
                    WriteFile(temp, new byte[0]);
                    ProtectKeyFile(temp);
                }
                WriteFile(temp, wrapped);
            }
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
            File.Move(temp, KeyPath);
        }

        /// <summary>Forgets every file. Used when LOTUS is uninstalled.</summary>
        public void Erase()
        {
            Delete(StorePath, BackupPath, PendingPath, KeyPath, KeyPath + ".tmp", LedgerPath, LedgerPath + ".bak",
                LedgerPath + ".tmp", OldLedgerPath, OldLedgerPath + ".bak", OldLedgerPath + ".tmp");
            key = null;
            lastWritten = null;
        }

        private static void Delete(params string[] paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>How the store key is wrapped before it is written to disk.</summary>
    public interface IKeyWrap
    {
        byte[] Wrap(byte[] key);
        /// <summary>Throws CryptographicException or FormatException when the bytes are not a wrapped key.</summary>
        byte[] Unwrap(byte[] wrapped);
    }
}
