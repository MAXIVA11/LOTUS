using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>
    /// A pretend USB bus that behaves the way Windows was observed to behave on
    /// real hardware. The unit tests run the enforcer against it, and demo mode
    /// uses it so the app can be tried without touching real devices.
    /// </summary>
    public sealed class SimulatedBackend : IDeviceBackend
    {
        private readonly object gate = new object();
        private readonly List<UsbDevice> plugged = new List<UsbDevice>();
        private readonly Dictionary<string, UsbDevice> originals =
            new Dictionary<string, UsbDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> disabledMarks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Devices Windows refuses to disable while they run. Keyboards behave like this.</summary>
        public readonly HashSet<string> NotDisableable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Devices that cannot be switched off at all, to test the retry path.</summary>
        public readonly HashSet<string> RefuseBlock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Devices that cannot be switched back on, to test the retry path.</summary>
        public readonly HashSet<string> RefuseUnblock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int BlockCalls;
        public int UnblockCalls;
        public int ScanCalls;
        public int ReinstallCalls;
        /// <summary>Devices Windows has no driver for. It never gets them installed, whatever the rules say.</summary>
        public readonly HashSet<string> NoDriver = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The installation restrictions of the pretend Windows. When set, a device
        /// that was never installed here and that the restrictions cover is refused,
        /// the way the real Windows was seen to do it.
        /// </summary>
        public SimulatedInstallGate Gate;
        private readonly HashSet<string> installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> refusedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Devices Windows remembers from earlier, by instance ID.</summary>
        private readonly Dictionary<string, UsbDevice> remembered =
            new Dictionary<string, UsbDevice>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Adds a device Windows has seen before but that is not plugged in.</summary>
        public void Remember(UsbDevice device)
        {
            lock (gate)
            {
                UsbDevice copy = device.Clone();
                copy.InstanceId = Policy.NormalizeId(copy.InstanceId);
                remembered[copy.InstanceId] = copy;
            }
        }

        public void Plug(UsbDevice device)
        {
            lock (gate)
            {
                UsbDevice copy = device.Clone();
                copy.InstanceId = Policy.NormalizeId(copy.InstanceId);
                copy.Present = true;
                originals[copy.InstanceId] = copy.Clone();
                remembered[copy.InstanceId] = copy.Clone();
                Unplug(copy.InstanceId);
                if (disabledMarks.Contains(copy.InstanceId))
                {
                    // A disabled device comes back disabled.
                    SwitchOff(copy, UsbDevice.ProblemDisabled);
                }
                else
                {
                    StartOrRefuse(copy, UsbDevice.ProblemNotConfigured);
                }
                plugged.Add(copy);
            }
        }

        public void Unplug(string instanceId)
        {
            lock (gate)
            {
                plugged.RemoveAll(d => d.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>Tells the pretend Windows that it has installed this device before.</summary>
        public void MarkInstalled(UsbDevice device)
        {
            lock (gate)
            {
                installed.Add(Policy.NormalizeId(device.InstanceId));
                foreach (string id in FunctionIds(device)) installed.Add(id);
            }
        }

        // ---------- installation restrictions ----------

        private static readonly HashSet<string> StorageCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "08", "06", "MTP" };

        private bool DeniesEverything => Gate != null && Gate.Owned && Gate.Denies(@"USB\COMPOSITE");
        private bool DeniesStorage => Gate != null && Gate.Owned && Gate.Denies(@"USB\Class_08");

        /// <summary>True when the pretend Windows would refuse to install this device as a whole.</summary>
        private bool RefusesDevice(UsbDevice device)
        {
            if (NoDriver.Contains(device.InstanceId)) return true;
            if (installed.Contains(device.InstanceId)) return false; // restrictions only count for new devices
            if (Gate == null || !Gate.Owned || Gate.Allows(device.InstanceId)) return false;
            if (DeniesEverything) return true;
            return DeniesStorage && !device.IsComposite && device.Functions.Exists(StorageCodes.Contains);
        }

        private bool RefusesFunction(string functionId, string code)
        {
            if (installed.Contains(functionId)) return false;
            if (Gate == null || !Gate.Owned || Gate.Allows(functionId)) return false;
            return DeniesEverything || (DeniesStorage && StorageCodes.Contains(code));
        }

        private static List<string> FunctionIds(UsbDevice device)
        {
            var ids = new List<string>();
            if (!device.IsComposite) return ids;
            for (int i = 0; i < device.Functions.Count; i++)
            {
                ids.Add("USB\\" + device.ModelId + "&MI_" + i.ToString("00") + "\\SIM&" + i.ToString("0000"));
            }
            return ids;
        }

        /// <summary>A device is switched on, or stays where Windows refuses to install it.</summary>
        private void StartOrRefuse(UsbDevice device, int refusedProblem)
        {
            if (RefusesDevice(device))
            {
                device.Started = false;
                device.Problem = refusedProblem;
                return;
            }
            device.Started = true;
            device.Problem = 0;
            installed.Add(device.InstanceId);
            // Now that the device runs, Windows meets its functions one by one.
            UsbDevice described = originals.TryGetValue(device.InstanceId, out UsbDevice original) ? original : device;
            List<string> ids = FunctionIds(described);
            for (int i = 0; i < ids.Count; i++)
            {
                if (RefusesFunction(ids[i], described.Functions[i])) refusedFunctions.Add(ids[i]);
                else
                {
                    refusedFunctions.Remove(ids[i]);
                    installed.Add(ids[i]);
                }
            }
        }

        public List<UsbChild> Children(string instanceId)
        {
            lock (gate)
            {
                var children = new List<UsbChild>();
                UsbDevice device = Find(instanceId);
                if (device == null || !device.Started) return children;
                UsbDevice described = originals.TryGetValue(device.InstanceId, out UsbDevice original) ? original : device;
                foreach (string id in FunctionIds(described))
                {
                    bool refused = refusedFunctions.Contains(id);
                    children.Add(new UsbChild
                    {
                        InstanceId = id,
                        Started = !refused,
                        Problem = refused ? UsbDevice.ProblemNotConfigured : 0
                    });
                }
                return children;
            }
        }

        public string ParentOf(string instanceId)
        {
            lock (gate)
            {
                foreach (UsbDevice device in plugged)
                {
                    UsbDevice described = originals.TryGetValue(device.InstanceId, out UsbDevice original) ? original : device;
                    if (FunctionIds(described).Exists(id => id.Equals(instanceId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return device.InstanceId;
                    }
                }
                return null;
            }
        }

        public DeviceOpResult Reinstall(string instanceId)
        {
            lock (gate)
            {
                ReinstallCalls++;
                UsbDevice device = Find(instanceId);
                if (device != null)
                {
                    // Windows forgets the device, the mark it kept for it included, and meets it again.
                    installed.Remove(device.InstanceId);
                    disabledMarks.Remove(device.InstanceId);
                    StartOrRefuse(device, UsbDevice.ProblemNotConfigured);
                    return DeviceOpResult.Success();
                }
                string parentId = ParentOf(instanceId);
                if (parentId == null) return DeviceOpResult.Fail("Could not find the device.");
                UsbDevice parent = Find(parentId);
                UsbDevice described = originals.TryGetValue(parent.InstanceId, out UsbDevice original) ? original : parent;
                List<string> ids = FunctionIds(described);
                int index = ids.FindIndex(id => id.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
                installed.Remove(ids[index]);
                if (RefusesFunction(ids[index], described.Functions[index])) refusedFunctions.Add(ids[index]);
                else
                {
                    refusedFunctions.Remove(ids[index]);
                    installed.Add(ids[index]);
                }
                return DeviceOpResult.Success();
            }
        }

        public bool IsPlugged(string instanceId)
        {
            lock (gate) return Find(instanceId) != null;
        }

        /// <summary>The disabled mark Windows keeps for a device, connected or not.</summary>
        public bool HasDisabledMark(string instanceId)
        {
            lock (gate) return disabledMarks.Contains(instanceId);
        }

        /// <summary>Switches a device off as if someone used Device Manager.</summary>
        public void DisableByHand(string instanceId)
        {
            lock (gate)
            {
                UsbDevice device = Find(instanceId);
                if (device == null) return;
                disabledMarks.Add(device.InstanceId);
                SwitchOff(device, UsbDevice.ProblemDisabled);
            }
        }

        public List<UsbDevice> Scan()
        {
            lock (gate)
            {
                ScanCalls++;
                var copy = new List<UsbDevice>();
                foreach (UsbDevice device in plugged) copy.Add(device.Clone());
                return copy;
            }
        }

        public UsbDevice ScanOne(string instanceId)
        {
            lock (gate) return Find(instanceId)?.Clone();
        }

        public List<UsbDevice> ScanAbsent()
        {
            lock (gate)
            {
                var absent = new List<UsbDevice>();
                foreach (UsbDevice device in remembered.Values)
                {
                    if (Find(device.InstanceId) != null) continue;
                    UsbDevice copy = device.Clone();
                    copy.Present = false;
                    copy.Started = false;
                    copy.Problem = disabledMarks.Contains(copy.InstanceId) ? UsbDevice.ProblemDisabled : 0;
                    if (copy.IsComposite && !copy.Described)
                    {
                        // Windows does not keep the interface list of an unplugged composite device.
                        copy.Functions = new List<string>();
                        copy.Kinds = DeviceKinds.None;
                        copy.FunctionsKnown = false;
                    }
                    absent.Add(copy);
                }
                return absent;
            }
        }

        public DeviceOpResult BlockAbsent(string instanceId)
        {
            lock (gate)
            {
                if (Find(instanceId) != null) return DeviceOpResult.Fail("The device is connected.");
                if (!remembered.ContainsKey(instanceId))
                {
                    return new DeviceOpResult { Ok = false, Gone = true, Message = "Could not find the device record." };
                }
                disabledMarks.Add(Policy.NormalizeId(instanceId));
                return DeviceOpResult.Success();
            }
        }

        public DeviceOpResult Block(string instanceId, out BlockMethod method)
        {
            lock (gate)
            {
                BlockCalls++;
                method = BlockMethod.Disabled;
                UsbDevice device = Find(instanceId);
                if (device == null) return DeviceOpResult.Fail("Could not find the device.");
                if (RefuseBlock.Contains(instanceId))
                {
                    return DeviceOpResult.Fail("A program is still using the device.");
                }
                // Real keyboards refuse the disable, get removed, and then accept the
                // disable once their driver is gone. The end result is the same mark.
                disabledMarks.Add(device.InstanceId);
                SwitchOff(device, NotDisableable.Contains(instanceId)
                    ? UsbDevice.ProblemRemoved
                    : UsbDevice.ProblemDisabled);
                return DeviceOpResult.Success();
            }
        }

        public DeviceOpResult Unblock(string instanceId)
        {
            lock (gate)
            {
                UnblockCalls++;
                if (RefuseUnblock.Contains(instanceId))
                {
                    return DeviceOpResult.Fail("The device did not start again.");
                }
                bool hadMark = disabledMarks.Remove(instanceId);
                UsbDevice device = Find(instanceId);
                if (device == null)
                {
                    return new DeviceOpResult { Ok = true, Gone = !hadMark };
                }
                if (originals.TryGetValue(device.InstanceId, out UsbDevice original))
                {
                    device.Functions = new List<string>(original.Functions);
                    device.Kinds = original.Kinds;
                    device.FunctionsKnown = original.FunctionsKnown;
                    device.Name = original.Name;
                }
                // Switched on again. A device Windows never installed is still not installed.
                StartOrRefuse(device, UsbDevice.ProblemFailedInstall);
                return DeviceOpResult.Success();
            }
        }

        private UsbDevice Find(string instanceId)
        {
            foreach (UsbDevice device in plugged)
            {
                if (device.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)) return device;
            }
            return null;
        }

        private static void SwitchOff(UsbDevice device, int problem)
        {
            device.Problem = problem;
            device.Started = false;
            if (device.IsComposite && !device.Described)
            {
                // A composite device that is off no longer lists its interfaces.
                device.Functions = new List<string>();
                device.Kinds = DeviceKinds.None;
                device.FunctionsKnown = false;
            }
        }
    }

    /// <summary>In-memory installation restrictions for tests and demo mode.</summary>
    public sealed class SimulatedInstallGate : IInstallGate
    {
        private InstallPlan plan;
        private readonly HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool Supported { get; set; } = true;
        /// <summary>Pretend that an administrator or Group Policy set restrictions of their own.</summary>
        public bool ForeignPolicy;
        public int ApplyCalls;
        public int RemoveCalls;

        public bool Owned => plan != null;
        public bool Foreign => ForeignPolicy && plan == null;
        public InstallPlan Plan => plan;

        public bool Denies(string id)
        {
            return plan != null && plan.Deny.Exists(d => d.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        public bool Allows(string instanceId)
        {
            return allowed.Contains(instanceId ?? "");
        }

        public bool Matches(InstallPlan other)
        {
            if (plan == null || other == null) return false;
            return new HashSet<string>(plan.Deny, StringComparer.OrdinalIgnoreCase).SetEquals(other.Deny) &&
                   allowed.SetEquals(other.AllowInstances);
        }

        public void Apply(InstallPlan next)
        {
            if (ForeignPolicy) throw new InvalidOperationException("Someone else's restrictions are in force.");
            ApplyCalls++;
            plan = next;
            allowed.Clear();
            foreach (string id in next.AllowInstances) allowed.Add(id);
        }

        public void AllowInstance(string instanceId)
        {
            if (plan != null && !string.IsNullOrEmpty(instanceId)) allowed.Add(instanceId.ToUpperInvariant());
        }

        public void Remove()
        {
            if (plan == null) return;
            RemoveCalls++;
            plan = null;
            allowed.Clear();
        }
    }

    /// <summary>An in-memory read-only switch for tests and demo mode.</summary>
    public sealed class SimulatedReadOnlyPolicy : IReadOnlyPolicy
    {
        public bool Applied;
        public bool Throw;
        public int ApplyCalls;
        public int RemoveCalls;

        public bool IsApplied()
        {
            if (Throw) throw new InvalidOperationException("The registry is not available.");
            return Applied;
        }

        public void Apply()
        {
            if (Throw) throw new InvalidOperationException("The registry is not available.");
            Applied = true;
            ApplyCalls++;
        }

        public void Remove()
        {
            if (Throw) throw new InvalidOperationException("The registry is not available.");
            Applied = false;
            RemoveCalls++;
        }
    }
}
