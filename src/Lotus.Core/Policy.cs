using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Lotus.Core
{
    public enum StorageMode
    {
        /// <summary>Removable storage works normally.</summary>
        Allow,
        /// <summary>Files can be opened and copied off removable storage, but nothing can be written to it.</summary>
        ReadOnly,
        /// <summary>Removable storage is disabled unless the device is trusted.</summary>
        Block
    }

    public enum PnpMode
    {
        /// <summary>Any USB device works.</summary>
        Allow,
        /// <summary>Only trusted USB devices work.</summary>
        Block
    }

    public enum TrustKind
    {
        /// <summary>One physical device, matched by its instance ID.</summary>
        Device,
        /// <summary>Every device with the same vendor and product ID.</summary>
        Model,
        /// <summary>Every device of one vendor, whatever the product.</summary>
        Vendor
    }

    public sealed class TrustEntry
    {
        public TrustKind Kind;
        /// <summary>
        /// An instance ID for Device entries, VID_xxxx&amp;PID_xxxx for Model entries and
        /// VID_xxxx for Vendor entries. Upper case.
        /// </summary>
        public string Id = "";
        public string Name = "";
        public DateTime AddedUtc = DateTime.UtcNow;
        /// <summary>
        /// The USB functions the device had when it was trusted, as class codes. A
        /// device that later shows up with a function that is not in this list is
        /// no longer covered, because a "trusted headset" that suddenly is a
        /// keyboard too is exactly what a hostile device looks like. Empty means
        /// the entry was made without this information and covers any function.
        /// </summary>
        public List<string> Functions = new List<string>();

        public bool Matches(UsbDevice device)
        {
            if (device == null) return false;
            string target = Kind == TrustKind.Device ? device.InstanceId
                : Kind == TrustKind.Model ? device.ModelId
                : "VID_" + device.Vid;
            // A device that did not say who made it matches no vendor.
            if (Kind == TrustKind.Vendor && string.IsNullOrEmpty(device.Vid)) return false;
            return string.Equals(Id, target, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>How the kind is written in a saved policy.</summary>
        public static string KindToText(TrustKind kind)
        {
            switch (kind)
            {
                case TrustKind.Model: return "model";
                case TrustKind.Vendor: return "vendor";
                default: return "device";
            }
        }

        /// <summary>
        /// Makes a trust entry from what a person typed, for a device LOTUS may
        /// never have seen. Such an entry records no functions, so it covers the
        /// device whatever it turns out to do.
        ///
        /// A vendor is four hex digits, with or without "VID_" or "0x" in front.
        /// A model is a vendor and a product: 046D:C52B, 046D C52B or
        /// VID_046D&amp;PID_C52B. A device is its instance path, as Device Manager
        /// or "lotus devices" shows it: USB\VID_0781&amp;PID_5567\4C530001230505116353.
        /// </summary>
        public static bool TryParse(TrustKind kind, string text, string name, out TrustEntry entry, out string error)
        {
            entry = null;
            error = null;
            string typed = (text ?? "").Trim().ToUpperInvariant();
            if (typed.Length == 0)
            {
                error = "Nothing was entered.";
                return false;
            }
            if (typed.Length > 200)
            {
                error = "That is too long to be a USB ID.";
                return false;
            }

            string id;
            string label;
            switch (kind)
            {
                case TrustKind.Vendor:
                {
                    string vid = HexPart(typed, "VID");
                    if (vid == null)
                    {
                        error = "A vendor ID is four characters from 0 to 9 and A to F, for example 046D.";
                        return false;
                    }
                    id = "VID_" + vid;
                    label = "Every device from vendor " + vid;
                    break;
                }
                case TrustKind.Model:
                {
                    string rest = typed.StartsWith(@"USB\", StringComparison.Ordinal) ? typed.Substring(4) : typed;
                    string[] parts = rest.Split(new[] { ':', '&', ' ', '/', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    string vid = parts.Length == 2 ? HexPart(parts[0], "VID") : null;
                    string pid = parts.Length == 2 ? HexPart(parts[1], "PID") : null;
                    if (vid == null || pid == null)
                    {
                        error = "A model is a vendor ID and a product ID, four characters each, for example 046D:C52B.";
                        return false;
                    }
                    id = "VID_" + vid + "&PID_" + pid;
                    label = "Every " + vid + ":" + pid;
                    break;
                }
                default:
                {
                    string[] parts = typed.Split('\\');
                    string[] model = parts.Length == 3 ? parts[1].Split('&') : new string[0];
                    if (parts.Length == 3 && parts[0] == "USB" && model.Length > 2)
                    {
                        error = "That is the path of one function of a device. Use the path of the device itself, " +
                                "the one without \"&MI_\" in it.";
                        return false;
                    }
                    bool shaped = parts.Length == 3 && parts[0] == "USB" && model.Length == 2 &&
                                  model[0].StartsWith("VID_", StringComparison.Ordinal) && IsHex4(model[0].Substring(4)) &&
                                  model[1].StartsWith("PID_", StringComparison.Ordinal) && IsHex4(model[1].Substring(4)) &&
                                  parts[2].Length > 0;
                    if (shaped)
                    {
                        foreach (char c in parts[2])
                        {
                            if (c <= ' ' || c > '~') shaped = false;
                        }
                    }
                    if (!shaped)
                    {
                        error = "An instance path looks like USB\\VID_0781&PID_5567\\4C530001230505116353: " +
                                "USB, the vendor and product, and the serial number or port.";
                        return false;
                    }
                    id = typed;
                    label = typed;
                    break;
                }
            }

            string cleanName = TextSafety.Clean(name ?? "");
            entry = new TrustEntry
            {
                Kind = kind,
                Id = Policy.NormalizeId(id),
                Name = cleanName.Length > 0 ? cleanName : label,
                AddedUtc = DateTime.UtcNow
            };
            return true;
        }

        /// <summary>Four hex digits, with an optional "VID_", "VID:", "VID=" or "0x" in front. Null when it is not that.</summary>
        private static string HexPart(string text, string prefix)
        {
            string value = text.Trim();
            foreach (string lead in new[] { prefix + "_", prefix + ":", prefix + "=", prefix, "0X" })
            {
                if (value.StartsWith(lead, StringComparison.Ordinal))
                {
                    value = value.Substring(lead.Length);
                    break;
                }
            }
            return IsHex4(value) ? value : null;
        }

        private static bool IsHex4(string text)
        {
            if (text == null || text.Length != 4) return false;
            foreach (char c in text)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        /// <summary>True when the device does nothing beyond what it did when it was trusted.</summary>
        public bool Covers(UsbDevice device)
        {
            if (Functions.Count == 0 || device == null || !device.FunctionsKnown) return true;
            foreach (string function in device.Functions)
            {
                if (!Functions.Contains(function)) return false;
            }
            return true;
        }

        public static TrustEntry ForDevice(UsbDevice device)
        {
            return new TrustEntry
            {
                Kind = TrustKind.Device,
                Id = Policy.NormalizeId(device.InstanceId),
                Name = TextSafety.Clean(device.Name),
                AddedUtc = DateTime.UtcNow,
                Functions = PinFor(device)
            };
        }

        public static TrustEntry ForModel(UsbDevice device)
        {
            return new TrustEntry
            {
                Kind = TrustKind.Model,
                Id = Policy.NormalizeId(device.ModelId),
                Name = TextSafety.Clean(device.Name),
                AddedUtc = DateTime.UtcNow,
                Functions = PinFor(device)
            };
        }

        private static List<string> PinFor(UsbDevice device)
        {
            return device.FunctionsKnown ? new List<string>(device.Functions) : new List<string>();
        }
    }

    /// <summary>
    /// The whole LOTUS policy. It is deliberately small: one master switch, one
    /// setting for removable storage, one for every other USB device, and a list of
    /// trusted devices that are exempt from blocking.
    /// </summary>
    public sealed class Policy
    {
        public const int CurrentVersion = 1;
        /// <summary>Far more than anyone needs, and small enough that a hostile file cannot stall the service.</summary>
        public const int MaxTrusted = 5000;

        public int Version = CurrentVersion;
        /// <summary>Master switch. While this is off LOTUS blocks nothing.</summary>
        public bool Armed;
        public StorageMode Storage = StorageMode.Allow;
        public PnpMode Pnp = PnpMode.Allow;
        /// <summary>
        /// Keeps keyboards and mice working when Pnp is Block, so a new keyboard can
        /// always be used to reach LOTUS. Turn it off to stop rogue keyboards as well.
        /// </summary>
        public bool AllowInput = true;
        /// <summary>
        /// Has Windows refuse to install USB devices the policy blocks, so that a
        /// device this PC has never seen does not start at all. Devices the policy
        /// allows are let through one by one. Off unless someone chooses it.
        /// </summary>
        public bool Strict;
        public List<TrustEntry> Trusted = new List<TrustEntry>();

        public static string NormalizeId(string id)
        {
            return (id ?? "").Trim().ToUpperInvariant();
        }

        /// <summary>The entry that makes this device trusted, or null.</summary>
        public TrustEntry FindTrust(UsbDevice device)
        {
            return FindTrust(device, out TrustEntry _);
        }

        /// <param name="outgrown">
        /// Set when an entry names this device but the device now has a function it
        /// did not have when it was trusted. Such an entry does not count.
        /// </param>
        public TrustEntry FindTrust(UsbDevice device, out TrustEntry outgrown)
        {
            outgrown = null;
            foreach (TrustEntry entry in Trusted)
            {
                if (!entry.Matches(device)) continue;
                if (entry.Covers(device)) return entry;
                outgrown = entry;
            }
            return null;
        }

        public bool IsTrusted(UsbDevice device)
        {
            return FindTrust(device) != null;
        }

        /// <summary>Adds a trust entry unless an identical one is already there.</summary>
        public bool AddTrust(TrustEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id)) return false;
            entry.Id = NormalizeId(entry.Id);
            foreach (TrustEntry existing in Trusted)
            {
                if (existing.Kind == entry.Kind && existing.Id == entry.Id) return false;
            }
            Trusted.Add(entry);
            return true;
        }

        /// <summary>
        /// Puts every given device on the trusted list and returns the entries
        /// that were new. A device without a serial number has a different
        /// identity in every port, so its model is trusted instead.
        /// </summary>
        public List<TrustEntry> TrustAll(IEnumerable<UsbDevice> devices)
        {
            var added = new List<TrustEntry>();
            if (devices == null) return added;
            foreach (UsbDevice device in devices)
            {
                if (device == null || string.IsNullOrEmpty(device.InstanceId)) continue;
                if (IsTrusted(device)) continue;
                TrustEntry entry = device.HasSerial ? TrustEntry.ForDevice(device) : TrustEntry.ForModel(device);
                if (Trusted.Count < MaxTrusted && AddTrust(entry)) added.Add(entry);
            }
            return added;
        }

        public bool RemoveTrust(TrustKind kind, string id)
        {
            string normalized = NormalizeId(id);
            return Trusted.RemoveAll(e => e.Kind == kind && e.Id == normalized) > 0;
        }

        /// <summary>True when the switches match. The trusted list is not compared.</summary>
        public bool SettingsEqual(Policy other)
        {
            return other != null && Armed == other.Armed && Storage == other.Storage &&
                   Pnp == other.Pnp && AllowInput == other.AllowInput && Strict == other.Strict;
        }

        public Policy Clone()
        {
            var copy = new Policy
            {
                Version = Version,
                Armed = Armed,
                Storage = Storage,
                Pnp = Pnp,
                AllowInput = AllowInput,
                Strict = Strict
            };
            foreach (TrustEntry entry in Trusted)
            {
                copy.Trusted.Add(new TrustEntry
                {
                    Kind = entry.Kind,
                    Id = entry.Id,
                    Name = entry.Name,
                    AddedUtc = entry.AddedUtc,
                    Functions = new List<string>(entry.Functions)
                });
            }
            return copy;
        }

        // ---------- JSON ----------

        public string ToJson()
        {
            var trusted = new List<object>();
            foreach (TrustEntry entry in Trusted)
            {
                trusted.Add(new Dictionary<string, object>
                {
                    ["kind"] = TrustEntry.KindToText(entry.Kind),
                    ["id"] = entry.Id,
                    ["name"] = entry.Name ?? "",
                    ["added"] = Json.FormatTime(entry.AddedUtc),
                    ["functions"] = new List<object>(entry.Functions)
                });
            }
            var root = new Dictionary<string, object>
            {
                ["version"] = Version,
                ["armed"] = Armed,
                ["storage"] = StorageToText(Storage),
                ["pnp"] = Pnp == PnpMode.Block ? "block" : "allow",
                ["allowInput"] = AllowInput,
                ["strict"] = Strict,
                ["trusted"] = trusted
            };
            return Json.Write(root);
        }

        /// <summary>
        /// Reads a policy. Unknown fields are ignored. A value that is present but not
        /// understood is an error, because guessing at a security setting is worse than
        /// refusing the file.
        /// </summary>
        public static Policy FromJson(string text)
        {
            Dictionary<string, object> root = Json.AsObject(Json.Parse(text));
            if (root == null) throw new FormatException("The policy file must contain a JSON object.");

            var policy = new Policy();
            policy.Version = Json.GetInt(root, "version", CurrentVersion);
            if (policy.Version > CurrentVersion)
            {
                throw new FormatException("The policy file was written by a newer version of LOTUS.");
            }
            policy.Version = CurrentVersion;
            policy.Armed = RequireBool(root, "armed", false);
            policy.AllowInput = RequireBool(root, "allowInput", true);
            // Off unless the policy says otherwise, like in a new policy.
            policy.Strict = RequireBool(root, "strict", false);

            string storage = Json.GetString(root, "storage", "allow");
            switch (storage.ToLowerInvariant())
            {
                case "allow": policy.Storage = StorageMode.Allow; break;
                case "readonly": policy.Storage = StorageMode.ReadOnly; break;
                case "block": policy.Storage = StorageMode.Block; break;
                default: throw new FormatException("Unknown storage setting \"" + storage + "\".");
            }

            string pnp = Json.GetString(root, "pnp", "allow");
            switch (pnp.ToLowerInvariant())
            {
                case "allow": policy.Pnp = PnpMode.Allow; break;
                case "block": policy.Pnp = PnpMode.Block; break;
                default: throw new FormatException("Unknown pnp setting \"" + pnp + "\".");
            }

            foreach (object item in Json.GetList(root, "trusted"))
            {
                Dictionary<string, object> obj = Json.AsObject(item);
                if (obj == null) continue;
                string kind = Json.GetString(obj, "kind", "device").ToLowerInvariant();
                string id = NormalizeId(Json.GetString(obj, "id", ""));
                if (id.Length == 0) continue;
                if (kind != "device" && kind != "model" && kind != "vendor")
                {
                    throw new FormatException("Unknown trust kind \"" + kind + "\".");
                }
                var entry = new TrustEntry
                {
                    Kind = kind == "model" ? TrustKind.Model : kind == "vendor" ? TrustKind.Vendor : TrustKind.Device,
                    Id = id,
                    Name = TextSafety.Clean(Json.GetString(obj, "name", "")),
                    AddedUtc = Json.ParseTime(Json.GetString(obj, "added", null), DateTime.UtcNow)
                };
                foreach (object function in Json.GetList(obj, "functions"))
                {
                    if (function is string code && code.Length > 0 && code.Length <= 8 &&
                        entry.Functions.Count < 64 && !entry.Functions.Contains(code.ToUpperInvariant()))
                    {
                        entry.Functions.Add(code.ToUpperInvariant());
                    }
                }
                if (policy.Trusted.Count >= MaxTrusted)
                {
                    throw new FormatException("The trusted list is longer than " + MaxTrusted + " entries.");
                }
                policy.AddTrust(entry);
            }
            return policy;
        }

        private static bool RequireBool(Dictionary<string, object> root, string key, bool fallback)
        {
            if (!root.TryGetValue(key, out object value)) return fallback;
            if (value is bool b) return b;
            throw new FormatException("\"" + key + "\" must be true or false.");
        }

        public static string StorageToText(StorageMode mode)
        {
            switch (mode)
            {
                case StorageMode.ReadOnly: return "readonly";
                case StorageMode.Block: return "block";
                default: return "allow";
            }
        }
    }

    public enum PolicySource
    {
        File,
        Backup,
        /// <summary>No usable file was found, so the built-in disarmed policy is in use.</summary>
        Default
    }

    public sealed class PolicyLoadResult
    {
        public Policy Policy;
        public PolicySource Source;
        /// <summary>Why the main file could not be used, or null when it loaded.</summary>
        public string Error;
    }

    /// <summary>Reads and writes the policy file.</summary>
    public static class PolicyStore
    {
        public static string BackupPath(string path)
        {
            return path + ".bak";
        }

        /// <summary>
        /// Loads the policy. If the file is damaged, the last good copy is used. If
        /// there is no good copy either, LOTUS falls back to a disarmed policy and
        /// reports why.
        /// </summary>
        public static PolicyLoadResult Load(string path)
        {
            var result = new PolicyLoadResult();
            try
            {
                if (File.Exists(path))
                {
                    result.Policy = Policy.FromJson(ReadShared(path));
                    result.Source = PolicySource.File;
                    return result;
                }
                result.Error = null;
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Error = ex.Message;
            }

            string backup = BackupPath(path);
            try
            {
                if (File.Exists(backup))
                {
                    result.Policy = Policy.FromJson(ReadShared(backup));
                    result.Source = PolicySource.Backup;
                    return result;
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Error = (result.Error == null ? "" : result.Error + " ") + "The backup failed too: " + ex.Message;
            }

            result.Policy = new Policy();
            result.Source = PolicySource.Default;
            return result;
        }

        /// <summary>Writes the policy so that a crash can never leave half a file behind.</summary>
        public static void Save(string path, Policy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            AtomicFile.Write(path, policy.ToJson(), BackupPath(path));
        }

        internal static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }

    internal static class AtomicFile
    {
        /// <summary>
        /// Writes text to a temporary file next to the target and then swaps it in.
        /// When backupPath is given, the previous version is kept there.
        /// </summary>
        public static void Write(string path, string text, string backupPath)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, backupPath, true);
                    return;
                }
                catch (IOException)
                {
                    // File.Replace fails on some file systems. Fall back to copy and move.
                    if (backupPath != null) File.Copy(path, backupPath, true);
                    File.Delete(path);
                }
            }
            File.Move(temp, path);
        }
    }
}
