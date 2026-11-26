using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    public enum DeviceState
    {
        /// <summary>Working, and the policy has nothing against it.</summary>
        Allowed,
        /// <summary>Working because it is on the trusted list.</summary>
        Trusted,
        /// <summary>Switched off by LOTUS.</summary>
        Blocked,
        /// <summary>The policy says block, but the device is still running.</summary>
        Blocking,
        /// <summary>Switched off by someone else, for example in Device Manager. LOTUS leaves it alone.</summary>
        OffElsewhere,
        /// <summary>Just plugged in and not identified yet.</summary>
        Identifying
    }

    /// <summary>A device together with what the policy says about it.</summary>
    public sealed class DeviceStatus
    {
        public UsbDevice Device;
        public Decision Decision;
        public DeviceState State;
    }

    /// <summary>
    /// Shared view logic: the service and the app both need to turn "a device, the
    /// ledger and a policy" into "what is going on with this device".
    /// </summary>
    public static class DeviceView
    {
        public static DeviceStatus Resolve(Policy policy, UsbDevice device, Ledger ledger, TimeSpan seenFor)
        {
            LedgerEntry entry = ledger?.Get(device.InstanceId);
            // A device that is switched off no longer lists what it does. The ledger remembers.
            if (device.IsOff && entry != null) entry.Restore(device);

            var status = new DeviceStatus { Device = device };
            if (device.IsOff && entry == null)
            {
                status.Decision = Decision.Allow(Reason.PolicyAllows);
                status.State = DeviceState.OffElsewhere;
                return status;
            }

            status.Decision = PolicyEngine.Evaluate(policy, device, seenFor);
            switch (status.Decision.Verdict)
            {
                case Verdict.Block:
                    status.State = device.IsOff ? DeviceState.Blocked : DeviceState.Blocking;
                    break;
                case Verdict.Pending:
                    status.State = DeviceState.Identifying;
                    break;
                default:
                    // Allowed by the policy but still switched off by LOTUS: the
                    // service is about to turn it back on.
                    if (device.IsOff) status.State = DeviceState.Blocked;
                    else status.State = StateFor(status.Decision);
                    break;
            }
            return status;
        }

        public static DeviceState StateFor(Decision decision)
        {
            if (decision.Verdict == Verdict.Pending) return DeviceState.Identifying;
            return decision.Reason == Reason.Trusted ? DeviceState.Trusted : DeviceState.Allowed;
        }
    }

    public sealed class PassResult
    {
        public List<DeviceStatus> Devices = new List<DeviceStatus>();
        public int Blocked;
        public int Released;
        public int Failures;
        public bool ReadOnlyActive;
    }

    /// <summary>
    /// Applies a policy to the devices that are connected. One pass looks at every
    /// USB device, switches off the ones the policy blocks, switches back on the ones
    /// it no longer blocks, and keeps the Windows read-only setting in step.
    /// </summary>
    public sealed class Enforcer
    {
        private readonly IDeviceBackend backend;
        private readonly IReadOnlyPolicy readOnly;
        private readonly Ledger ledger;
        private readonly Action<Ledger> saveLedger;
        private readonly Action<LotusEvent> log;
        private readonly Func<DateTime> clock;

        private readonly Dictionary<string, DateTime> firstSeen =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> failures =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> arrivedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool firstPass = true;
        private string lastReadOnlyError;

        /// <summary>A failing device is reported on the first failure and then once every this many passes.</summary>
        private const int FailureLogInterval = 150;

        public Enforcer(IDeviceBackend backend, IReadOnlyPolicy readOnly, Ledger ledger, Action<Ledger> saveLedger,
            Action<LotusEvent> log, Func<DateTime> clock = null)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            this.readOnly = readOnly ?? throw new ArgumentNullException(nameof(readOnly));
            this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            this.saveLedger = saveLedger ?? (_ => { });
            this.log = log ?? (_ => { });
            this.clock = clock ?? (() => DateTime.UtcNow);
        }

        public Ledger Ledger => ledger;

        public PassResult RunOnce(Policy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            DateTime now = clock();
            var result = new PassResult();
            List<UsbDevice> devices = backend.Scan();

            TrackArrivals(devices, now);

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsbDevice device in devices)
            {
                present.Add(device.InstanceId);
                TimeSpan seenFor = now - firstSeen[device.InstanceId];
                DeviceStatus status = DeviceView.Resolve(policy, device, ledger, seenFor);
                result.Devices.Add(status);
                if (status.State == DeviceState.OffElsewhere) continue;

                LedgerEntry entry = ledger.Get(device.InstanceId);
                if (status.Decision.IsBlock)
                {
                    if (device.IsOff)
                    {
                        StillBlocked(device, status, entry);
                        continue;
                    }
                    Block(device, status, entry, now, result);
                }
                else if (entry != null)
                {
                    Release(device, entry, status, result);
                }
            }

            ReleaseAbsent(policy, present, result);
            SyncReadOnly(policy, devices, now);
            result.ReadOnlyActive = policy.Armed && policy.Storage == StorageMode.ReadOnly &&
                                    lastReadOnlyError == null;
            if (lastReadOnlyError != null) result.Failures++;

            firstPass = false;
            return result;
        }

        private void TrackArrivals(List<UsbDevice> devices, DateTime now)
        {
            arrivedNow.Clear();
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsbDevice device in devices)
            {
                current.Add(device.InstanceId);
                LedgerEntry known = ledger.Get(device.InstanceId);
                string name = known != null && !string.IsNullOrEmpty(known.Name) ? known.Name : device.Name;
                // A device gets its proper name a moment after it appears, so keep the latest.
                names[device.InstanceId] = name;
                if (firstSeen.ContainsKey(device.InstanceId)) continue;
                // Devices that were already there when the service started have had
                // plenty of time to identify themselves.
                firstSeen[device.InstanceId] = firstPass ? now - PolicyEngine.IdentifyGrace : now;
                if (firstPass) continue;
                arrivedNow.Add(device.InstanceId);
                Log(EventTypes.Connected, name + " was connected.", device.InstanceId, name);
            }

            foreach (string id in new List<string>(firstSeen.Keys))
            {
                if (current.Contains(id)) continue;
                names.TryGetValue(id, out string name);
                firstSeen.Remove(id);
                failures.Remove(id);
                names.Remove(id);
                if (!firstPass)
                {
                    Log(EventTypes.Removed, (string.IsNullOrEmpty(name) ? "A device" : name) + " was disconnected.",
                        id, name ?? "");
                }
            }
        }

        /// <summary>The device is already off and the policy still blocks it.</summary>
        private void StillBlocked(UsbDevice device, DeviceStatus status, LedgerEntry entry)
        {
            if (entry == null) return;
            if (entry.Reason != status.Decision.Reason || entry.Restart)
            {
                entry.Reason = status.Decision.Reason;
                entry.Restart = false;
                saveLedger(ledger);
            }
            // Someone plugged a blocked device back in. Say so, or they are left
            // wondering why nothing happens.
            if (arrivedNow.Contains(device.InstanceId))
            {
                Log(EventTypes.Blocked,
                    device.Name + " is blocked because " + PolicyEngine.BlockReason(status.Decision.Reason) + ".",
                    device.InstanceId, device.Name);
            }
        }

        private void Block(UsbDevice device, DeviceStatus status, LedgerEntry entry, DateTime now, PassResult result)
        {
            if (entry == null)
            {
                // Write the ledger first. If LOTUS stops halfway, the next run still
                // knows this device is its responsibility.
                entry = LedgerEntry.From(device, status.Decision.Reason, now);
                ledger.Add(entry);
                saveLedger(ledger);
            }
            else if (entry.Restart || entry.Reason != status.Decision.Reason)
            {
                entry.Restart = false;
                entry.Reason = status.Decision.Reason;
                saveLedger(ledger);
            }

            DeviceOpResult op = backend.Block(device.InstanceId, out BlockMethod method);
            if (op.Ok)
            {
                if (entry.Method != method)
                {
                    entry.Method = method;
                    saveLedger(ledger);
                }
                failures.Remove(device.InstanceId);
                device.Problem = method == BlockMethod.Removed ? UsbDevice.ProblemRemoved : UsbDevice.ProblemDisabled;
                device.Started = false;
                status.State = DeviceState.Blocked;
                result.Blocked++;
                Log(EventTypes.Blocked,
                    device.Name + " was blocked because " + PolicyEngine.BlockReason(status.Decision.Reason) + ".",
                    device.InstanceId, device.Name);
                return;
            }

            result.Failures++;
            if (ShouldLogFailure(device.InstanceId))
            {
                Log(EventTypes.Warning, "Could not block " + device.Name + ". " + op.Message + " LOTUS keeps trying.",
                    device.InstanceId, device.Name);
            }
        }

        private void Release(UsbDevice device, LedgerEntry entry, DeviceStatus status, PassResult result)
        {
            DeviceOpResult op = backend.Unblock(device.InstanceId);
            if (op.Ok)
            {
                bool silent = entry.Restart;
                ledger.Remove(device.InstanceId);
                saveLedger(ledger);
                failures.Remove(device.InstanceId);
                device.Problem = 0;
                device.Started = true;
                status.State = DeviceView.StateFor(status.Decision);
                if (!silent)
                {
                    result.Released++;
                    Log(EventTypes.Released, entry.Name + " is allowed again.", device.InstanceId, entry.Name);
                }
                return;
            }

            result.Failures++;
            if (ShouldLogFailure(device.InstanceId))
            {
                Log(EventTypes.Warning,
                    "Could not switch " + entry.Name + " back on. " + op.Message + " LOTUS keeps trying.",
                    device.InstanceId, entry.Name);
            }
        }

        /// <summary>
        /// A blocked device that has been unplugged stays marked as disabled inside
        /// Windows. When the policy stops blocking it, that mark has to go, or the
        /// device would stay dead the next time it is plugged in.
        /// </summary>
        private void ReleaseAbsent(Policy policy, HashSet<string> present, PassResult result)
        {
            foreach (LedgerEntry entry in ledger.Entries)
            {
                if (present.Contains(entry.InstanceId)) continue;
                Decision decision = PolicyEngine.Evaluate(policy, entry.ToDevice(), TimeSpan.MaxValue);
                if (decision.IsBlock) continue;

                DeviceOpResult op = backend.Unblock(entry.InstanceId);
                if (!op.Ok)
                {
                    result.Failures++;
                    if (ShouldLogFailure(entry.InstanceId))
                    {
                        Log(EventTypes.Warning, "Could not clear the block on " + entry.Name + ". " + op.Message,
                            entry.InstanceId, entry.Name);
                    }
                    continue;
                }
                ledger.Remove(entry.InstanceId);
                saveLedger(ledger);
                failures.Remove(entry.InstanceId);
                if (!entry.Restart)
                {
                    result.Released++;
                    Log(EventTypes.Released, entry.Name + " is allowed again. It is not connected right now.",
                        entry.InstanceId, entry.Name);
                }
            }
        }

        private void SyncReadOnly(Policy policy, List<UsbDevice> devices, DateTime now)
        {
            bool want = policy.Armed && policy.Storage == StorageMode.ReadOnly;
            try
            {
                bool applied = readOnly.IsApplied();
                if (want && !applied)
                {
                    readOnly.Apply();
                    ledger.ReadOnlyOwned = true;
                    saveLedger(ledger);
                    Log(EventTypes.ReadOnlyOn, "Removable storage is now read-only.", "", "");
                    RestartStorage(devices, now);
                }
                else if (!want && ledger.ReadOnlyOwned)
                {
                    if (applied) readOnly.Remove();
                    ledger.ReadOnlyOwned = false;
                    saveLedger(ledger);
                    if (applied)
                    {
                        Log(EventTypes.ReadOnlyOff, "Removable storage is writable again.", "", "");
                        RestartStorage(devices, now);
                    }
                }
                else if (want && !ledger.ReadOnlyOwned)
                {
                    // The setting was already there before LOTUS wanted it. From now on
                    // LOTUS looks after it, and removes it when read-only is turned off.
                    ledger.ReadOnlyOwned = true;
                    saveLedger(ledger);
                }
                lastReadOnlyError = null;
            }
            catch (Exception ex)
            {
                if (lastReadOnlyError != ex.Message)
                {
                    lastReadOnlyError = ex.Message;
                    Log(EventTypes.Error, "Could not change the read-only setting. " + ex.Message, "", "");
                }
            }
        }

        /// <summary>
        /// Windows applies the read-only setting when a drive is connected. Switching
        /// a connected drive off and on again has the same effect as replugging it.
        /// </summary>
        private void RestartStorage(List<UsbDevice> devices, DateTime now)
        {
            foreach (UsbDevice device in devices)
            {
                if (!device.HasStorage || device.IsOff || !device.Started || device.IsSystemDisk) continue;
                if (ledger.Contains(device.InstanceId)) continue;

                // The ledger entry makes sure the drive is switched back on even if
                // LOTUS is stopped between the two steps.
                LedgerEntry entry = LedgerEntry.From(device, Reason.PolicyAllows, now);
                entry.Restart = true;
                ledger.Add(entry);
                saveLedger(ledger);

                DeviceOpResult off = backend.Block(device.InstanceId, out BlockMethod _);
                DeviceOpResult on = backend.Unblock(device.InstanceId);
                if (on.Ok)
                {
                    ledger.Remove(device.InstanceId);
                    saveLedger(ledger);
                }
                if (!off.Ok)
                {
                    Log(EventTypes.Warning,
                        device.Name + " is in use, so the read-only change reaches it the next time it is connected.",
                        device.InstanceId, device.Name);
                }
            }
        }

        /// <summary>
        /// Switches every blocked device back on and removes the read-only setting.
        /// Used when LOTUS is uninstalled. Returns the number of things it could not undo.
        /// </summary>
        public int ReleaseEverything()
        {
            int problems = 0;
            foreach (LedgerEntry entry in ledger.Entries)
            {
                DeviceOpResult op = backend.Unblock(entry.InstanceId);
                if (op.Ok)
                {
                    ledger.Remove(entry.InstanceId);
                    if (!entry.Restart)
                    {
                        Log(EventTypes.Released, entry.Name + " is allowed again.", entry.InstanceId, entry.Name);
                    }
                }
                else
                {
                    problems++;
                    Log(EventTypes.Warning, "Could not switch " + entry.Name + " back on. " + op.Message,
                        entry.InstanceId, entry.Name);
                }
            }
            try
            {
                if (ledger.ReadOnlyOwned)
                {
                    if (readOnly.IsApplied()) readOnly.Remove();
                    ledger.ReadOnlyOwned = false;
                    Log(EventTypes.ReadOnlyOff, "Removable storage is writable again.", "", "");
                }
            }
            catch (Exception ex)
            {
                problems++;
                Log(EventTypes.Error, "Could not remove the read-only setting. " + ex.Message, "", "");
            }
            saveLedger(ledger);
            return problems;
        }

        private bool ShouldLogFailure(string id)
        {
            failures.TryGetValue(id, out int count);
            failures[id] = count + 1;
            return count % FailureLogInterval == 0;
        }

        private void Log(string type, string message, string deviceId, string deviceName)
        {
            log(new LotusEvent
            {
                TimeUtc = clock(),
                Type = type,
                Message = message,
                DeviceId = deviceId ?? "",
                DeviceName = deviceName ?? ""
            });
        }
    }
}
