using System;
using System.Collections.Generic;
using System.IO;

namespace Lotus.Core
{
    /// <summary>
    /// One device LOTUS has switched off. A disabled composite device no longer
    /// lists its interfaces, so the ledger keeps what was known about it at the
    /// moment it was blocked.
    /// </summary>
    public sealed class LedgerEntry
    {
        public string InstanceId = "";
        public string Name = "";
        public string Vendor = "";
        public string Vid = "";
        public string Pid = "";
        public string Serial = "";
        public bool HasSerial;
        public bool BuiltIn;
        public bool IsHub;
        public List<string> Functions = new List<string>();
        public DeviceKinds Kinds;
        public Reason Reason;
        /// <summary>How the device was switched off. Removed means Windows forgets it on replug.</summary>
        public BlockMethod Method = BlockMethod.Disabled;
        public DateTime SinceUtc = DateTime.UtcNow;
        /// <summary>
        /// The device was only switched off for a moment so that a read-only change
        /// reaches it. It is switched back on right away.
        /// </summary>
        public bool Restart;
        /// <summary>
        /// The device was marked as blocked while it was unplugged, so that it is
        /// already off when it comes back. Cleared once it has been seen connected.
        /// </summary>
        public bool Premarked;
        /// <summary>False when the device had not said what it does at the moment it was recorded.</summary>
        public bool KindsKnown = true;

        public static LedgerEntry From(UsbDevice device, Reason reason, DateTime nowUtc)
        {
            return new LedgerEntry
            {
                KindsKnown = device.FunctionsKnown,
                InstanceId = Policy.NormalizeId(device.InstanceId),
                Name = device.Name,
                Vendor = device.Vendor,
                Vid = device.Vid,
                Pid = device.Pid,
                Serial = device.Serial,
                HasSerial = device.HasSerial,
                BuiltIn = device.BuiltIn,
                IsHub = device.IsHub,
                Functions = new List<string>(device.Functions),
                Kinds = device.FunctionsKnown && device.Kinds != DeviceKinds.None ? device.Kinds : DeviceKinds.Other,
                Reason = reason,
                SinceUtc = nowUtc
            };
        }

        /// <summary>Rebuilds a device description for something that is not connected right now.</summary>
        public UsbDevice ToDevice()
        {
            return new UsbDevice
            {
                InstanceId = InstanceId,
                Name = Name,
                Vendor = Vendor,
                Vid = Vid,
                Pid = Pid,
                Serial = Serial,
                HasSerial = HasSerial,
                BuiltIn = BuiltIn,
                IsHub = IsHub,
                Functions = new List<string>(Functions),
                Kinds = Kinds,
                FunctionsKnown = KindsKnown,
                Present = false,
                Started = false,
                Problem = UsbDevice.ProblemDisabled
            };
        }

        /// <summary>
        /// Puts back what a disabled device can no longer report about itself: its
        /// real name and the list of things it does.
        /// </summary>
        public void Restore(UsbDevice device)
        {
            if (!string.IsNullOrEmpty(Name)) device.Name = Name;
            if (!string.IsNullOrEmpty(Vendor) && string.IsNullOrEmpty(device.Vendor)) device.Vendor = Vendor;
            if (device.Described)
            {
                // The device's own descriptors were read just now. They are newer and
                // more trustworthy than what was written down when it was blocked.
                foreach (string function in Functions)
                {
                    if (!device.Functions.Contains(function)) device.Functions.Add(function);
                }
                device.Kinds |= KindsKnown ? Kinds : DeviceKinds.None;
                return;
            }
            device.Functions = new List<string>(Functions);
            device.Kinds = Kinds;
            device.IsHub = IsHub;
            device.FunctionsKnown = KindsKnown;
        }
    }

    /// <summary>
    /// The list of devices LOTUS itself has disabled. LOTUS only ever re-enables
    /// devices that are in this list, so a device someone switched off by hand in
    /// Device Manager stays off.
    /// </summary>
    public sealed class Ledger
    {
        private readonly Dictionary<string, LedgerEntry> entries =
            new Dictionary<string, LedgerEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>LOTUS wrote the Windows read-only policy and has to remove it again.</summary>
        public bool ReadOnlyOwned;

        public int Count => entries.Count;

        public IEnumerable<LedgerEntry> Entries => new List<LedgerEntry>(entries.Values);

        public LedgerEntry Get(string instanceId)
        {
            return entries.TryGetValue(instanceId ?? "", out LedgerEntry entry) ? entry : null;
        }

        public bool Contains(string instanceId)
        {
            return entries.ContainsKey(instanceId ?? "");
        }

        public void Add(LedgerEntry entry)
        {
            entries[entry.InstanceId] = entry;
        }

        public bool Remove(string instanceId)
        {
            return entries.Remove(instanceId ?? "");
        }

        public string ToJson()
        {
            var list = new List<object>();
            foreach (LedgerEntry e in entries.Values)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = e.InstanceId,
                    ["name"] = e.Name ?? "",
                    ["vendor"] = e.Vendor ?? "",
                    ["vid"] = e.Vid ?? "",
                    ["pid"] = e.Pid ?? "",
                    ["serial"] = e.Serial ?? "",
                    ["hasSerial"] = e.HasSerial,
                    ["builtIn"] = e.BuiltIn,
                    ["hub"] = e.IsHub,
                    ["functions"] = new List<object>(e.Functions),
                    ["kinds"] = (int)e.Kinds,
                    ["reason"] = e.Reason.ToString(),
                    ["method"] = e.Method.ToString(),
                    ["since"] = Json.FormatTime(e.SinceUtc),
                    ["restart"] = e.Restart,
                    ["premarked"] = e.Premarked,
                    ["kindsKnown"] = e.KindsKnown
                });
            }
            return Json.Write(new Dictionary<string, object>
            {
                ["version"] = 1,
                ["readOnlyOwned"] = ReadOnlyOwned,
                ["blocked"] = list
            });
        }

        public static Ledger FromJson(string text)
        {
            var ledger = new Ledger();
            Dictionary<string, object> root = Json.AsObject(Json.Parse(text));
            if (root == null) throw new FormatException("The ledger file must contain a JSON object.");
            ledger.ReadOnlyOwned = Json.GetBool(root, "readOnlyOwned", false);
            foreach (object item in Json.GetList(root, "blocked"))
            {
                Dictionary<string, object> obj = Json.AsObject(item);
                if (obj == null) continue;
                string id = Policy.NormalizeId(Json.GetString(obj, "id", ""));
                if (id.Length == 0) continue;
                var entry = new LedgerEntry
                {
                    InstanceId = id,
                    Name = TextSafety.Clean(Json.GetString(obj, "name", "")),
                    Vendor = TextSafety.Clean(Json.GetString(obj, "vendor", ""), 48),
                    Vid = Json.GetString(obj, "vid", ""),
                    Pid = Json.GetString(obj, "pid", ""),
                    Serial = Json.GetString(obj, "serial", ""),
                    HasSerial = Json.GetBool(obj, "hasSerial", false),
                    BuiltIn = Json.GetBool(obj, "builtIn", false),
                    IsHub = Json.GetBool(obj, "hub", false),
                    Kinds = (DeviceKinds)Json.GetInt(obj, "kinds", (int)DeviceKinds.Other),
                    SinceUtc = Json.ParseTime(Json.GetString(obj, "since", null), DateTime.UtcNow),
                    Restart = Json.GetBool(obj, "restart", false),
                    Premarked = Json.GetBool(obj, "premarked", false),
                    KindsKnown = Json.GetBool(obj, "kindsKnown", true)
                };
                if (!Enum.TryParse(Json.GetString(obj, "reason", ""), out entry.Reason))
                {
                    entry.Reason = Reason.PnpBlocked;
                }
                if (!Enum.TryParse(Json.GetString(obj, "method", ""), out entry.Method))
                {
                    entry.Method = BlockMethod.Disabled;
                }
                foreach (object f in Json.GetList(obj, "functions"))
                {
                    if (f is string s) entry.Functions.Add(s);
                }
                ledger.Add(entry);
            }
            return ledger;
        }

        /// <summary>A separate copy, so one thread can read while another keeps working on the original.</summary>
        public Ledger Clone()
        {
            return FromJson(ToJson());
        }

        /// <summary>
        /// Loads the ledger, falling back to its backup copy. A missing file is an
        /// empty ledger. error is set when a file existed but could not be read.
        /// </summary>
        public static Ledger Load(string path, out string error)
        {
            error = null;
            foreach (string candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    return FromJson(PolicyStore.ReadShared(candidate));
                }
                catch (Exception ex) when (ex is FormatException || ex is IOException ||
                                           ex is UnauthorizedAccessException)
                {
                    error = (error == null ? "" : error + " ") + Path.GetFileName(candidate) + ": " + ex.Message;
                }
            }
            return new Ledger();
        }

        public void Save(string path)
        {
            AtomicFile.Write(path, ToJson(), path + ".bak");
        }
    }
}
