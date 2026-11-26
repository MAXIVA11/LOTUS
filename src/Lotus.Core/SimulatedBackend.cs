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
                    copy.Started = true;
                    copy.Problem = 0;
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
                device.Problem = 0;
                device.Started = true;
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
