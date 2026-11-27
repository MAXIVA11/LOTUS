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
        /// <summary>Devices the policy allows that Windows has not installed yet. LOTUS looks again soon.</summary>
        public int Pending;
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
        private readonly IInstallGate gate;

        private readonly Dictionary<string, DateTime> firstSeen =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> failures =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> arrivedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool firstPass = true;
        private string lastReadOnlyError;

        // Strict mode: Windows refuses to install USB devices, and LOTUS lets the
        // allowed ones through one by one.
        private bool gateOwned;
        private string lastGateNote;
        private readonly Dictionary<string, DateTime> admitted =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LetInState> lettingIn =
            new Dictionary<string, LetInState>(StringComparer.OrdinalIgnoreCase);
        private int pendingNow;

        /// <summary>Where LOTUS stands with a device it is letting through.</summary>
        private sealed class LetInState
        {
            /// <summary>When the device was named as an exception.</summary>
            public DateTime AllowedAt;
            /// <summary>When LOTUS last had Windows forget the device and find it again.</summary>
            public DateTime? ReinstalledAt;
            public int Attempts;
            public bool Reported;
        }
        /// <summary>How long a device LOTUS just let through stays on the list while Windows installs it.</summary>
        private static readonly TimeSpan AdmitGrace = TimeSpan.FromSeconds(60);
        private const int MaxAdmitAttempts = 3;
        /// <summary>
        /// Windows is in the middle of its own attempt to install a device when it
        /// reports the device. An exception named in that moment may still be in
        /// time, and then nothing more is needed. If it was too late, Windows has
        /// to be made to forget the device and find it again, but only once its
        /// own attempt is over: doing it earlier lets the old attempt mark the
        /// new device as failed.
        /// </summary>
        private static readonly TimeSpan InstallSettle = TimeSpan.FromSeconds(1);
        /// <summary>How long Windows gets for an installation LOTUS asked for before LOTUS asks again.</summary>
        private static readonly TimeSpan AdmitSettle = TimeSpan.FromSeconds(3);
        /// <summary>
        /// Devices that were not installed before LOTUS shut the door. Windows has
        /// its own reasons for those, usually a missing driver, and LOTUS leaves
        /// them alone.
        /// </summary>
        private readonly HashSet<string> notInstalledBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A failing device is reported on the first failure and then once every this many passes.</summary>
        private const int FailureLogInterval = 150;

        public Enforcer(IDeviceBackend backend, IReadOnlyPolicy readOnly, Ledger ledger, Action<Ledger> saveLedger,
            Action<LotusEvent> log, Func<DateTime> clock = null, IInstallGate gate = null)
        {
            this.gate = gate;
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
            pendingNow = 0;

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsbDevice device in devices)
            {
                present.Add(device.InstanceId);
                Handle(policy, device, now, result);
            }

            ReleaseAbsent(policy, present, result);
            SyncReadOnly(policy, devices, now);
            SyncGate(policy, result, now);
            result.Pending = pendingNow;
            result.ReadOnlyActive = policy.Armed && policy.Storage == StorageMode.ReadOnly &&
                                    lastReadOnlyError == null;
            if (lastReadOnlyError != null) result.Failures++;

            firstPass = false;
            return result;
        }

        /// <summary>Decides about one connected device and acts on the decision.</summary>
        private void Handle(Policy policy, UsbDevice device, DateTime now, PassResult result)
        {
            TimeSpan seenFor = now - firstSeen[device.InstanceId];
            DeviceStatus status = DeviceView.Resolve(policy, device, ledger, seenFor);
            result.Devices.Add(status);
            if (status.State == DeviceState.OffElsewhere) return;

            LedgerEntry entry = ledger.Get(device.InstanceId);
            if (entry != null && entry.Premarked)
            {
                // It was marked while unplugged and has now shown up. From here on it
                // is an ordinary blocked device.
                entry.Premarked = false;
                if (device.Described)
                {
                    entry.Functions = new List<string>(device.Functions);
                    entry.Kinds = device.Kinds;
                    entry.KindsKnown = true;
                }
                if (!string.IsNullOrEmpty(device.Name)) entry.Name = device.Name;
                saveLedger(ledger);
            }

            if (status.Decision.IsBlock)
            {
                if (device.IsOff)
                {
                    StillBlocked(device, status, entry);
                    return;
                }
                Block(device, status, entry, now, result);
            }
            else if (entry != null && status.Decision.Verdict != Verdict.Pending)
            {
                Release(device, entry, status, result);
            }
            else if (status.Decision.Verdict == Verdict.Allow)
            {
                Admit(device, now);
            }
        }

        /// <summary>
        /// Decides about a single device the instant Windows reports it, without
        /// looking at anything else. A full pass still follows, but by then a device
        /// the policy blocks is already off. Returns null when the ID is not a
        /// connected USB device.
        /// </summary>
        public DeviceStatus FastPass(Policy policy, string instanceId)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            // Before the first full pass nothing is known about what was already there.
            if (firstPass) return null;

            DateTime now = clock();
            UsbDevice device = backend.ScanOne(instanceId);
            if (device == null)
            {
                // Not a device of its own. It may be one function of a device that
                // Windows refused to install although the device itself is allowed.
                AdmitFunction(policy, instanceId, now);
                return null;
            }

            arrivedNow.Clear();
            NoteArrival(device, now);
            var result = new PassResult();
            Handle(policy, device, now, result);
            return result.Devices.Count > 0 ? result.Devices[0] : null;
        }

        /// <summary>
        /// Marks devices that are not connected right now, and that the policy would
        /// block on sight, as disabled. Windows then keeps them off from the first
        /// instant they are plugged in. Returns how many were marked.
        /// </summary>
        public int MarkAbsent(Policy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (!policy.Armed || (policy.Storage != StorageMode.Block && policy.Pnp != PnpMode.Block)) return 0;

            DateTime now = clock();
            var candidates = new List<LedgerEntry>();
            foreach (UsbDevice device in backend.ScanAbsent())
            {
                if (ledger.Contains(device.InstanceId)) continue;
                // Switched off by someone else. Not LOTUS's to manage.
                if (device.IsOff) continue;
                // No waiting here: only a device that is blocked whatever it turns out to be.
                Decision decision = PolicyEngine.Evaluate(policy, device, TimeSpan.Zero);
                if (!decision.IsBlock) continue;
                LedgerEntry entry = LedgerEntry.From(device, decision.Reason, now);
                entry.Premarked = true;
                candidates.Add(entry);
            }
            if (candidates.Count == 0) return 0;

            // Ledger first, so a stop halfway never leaves a mark nobody remembers.
            foreach (LedgerEntry entry in candidates) ledger.Add(entry);
            saveLedger(ledger);

            int marked = 0;
            foreach (LedgerEntry entry in candidates)
            {
                DeviceOpResult op = backend.BlockAbsent(entry.InstanceId);
                if (op.Ok) marked++;
                else ledger.Remove(entry.InstanceId);
            }
            saveLedger(ledger);
            return marked;
        }

        private void TrackArrivals(List<UsbDevice> devices, DateTime now)
        {
            arrivedNow.Clear();
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UsbDevice device in devices)
            {
                current.Add(device.InstanceId);
                NoteArrival(device, now);
            }

            foreach (string id in new List<string>(firstSeen.Keys))
            {
                if (current.Contains(id)) continue;
                names.TryGetValue(id, out string name);
                firstSeen.Remove(id);
                failures.Remove(id);
                names.Remove(id);
                lettingIn.Remove(id);
                notInstalledBefore.Remove(id);
                if (!firstPass)
                {
                    Log(EventTypes.Removed, (string.IsNullOrEmpty(name) ? "A device" : name) + " was disconnected.",
                        id, name ?? "");
                }
            }
        }

        private void NoteArrival(UsbDevice device, DateTime now)
        {
            LedgerEntry known = ledger.Get(device.InstanceId);
            string name = known != null && !string.IsNullOrEmpty(known.Name) ? known.Name : device.Name;
            // A device gets its proper name a moment after it appears, so keep the latest.
            names[device.InstanceId] = name;
            if (firstSeen.ContainsKey(device.InstanceId)) return;
            // Devices that were already there when the service started have had
            // plenty of time to identify themselves.
            firstSeen[device.InstanceId] = firstPass ? now - PolicyEngine.IdentifyGrace : now;
            if (firstPass) return;
            arrivedNow.Add(device.InstanceId);
            Log(EventTypes.Connected, name + " was connected.", device.InstanceId, name);
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
                    device.Name + " is blocked because " + PolicyEngine.BlockReason(status.Decision) + ".",
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
                    device.Name + " was blocked because " + PolicyEngine.BlockReason(status.Decision) + ".",
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
            // A device Windows refused to install before LOTUS switched it off is
            // installed the moment it is switched on. Name it as an exception
            // first, so that Windows does not refuse it again.
            if (gateOwned)
            {
                admitted[device.InstanceId] = clock();
                gate.AllowInstance(device.InstanceId);
            }
            DeviceOpResult op = backend.Unblock(device.InstanceId);
            if (op.Ok)
            {
                bool silent = entry.Restart || entry.Premarked;
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
                // An entry that never learned what the device does is only kept while
                // the policy blocks the device whatever it is.
                Decision decision = PolicyEngine.Evaluate(policy, entry.ToDevice(),
                    entry.KindsKnown ? TimeSpan.MaxValue : TimeSpan.Zero);
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
                if (!entry.Restart && !entry.Premarked)
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
            // The door for new devices opens first, or a device that is released
            // below would be refused again straight away.
            try
            {
                if (gate != null && gate.Owned) gate.Remove();
                gateOwned = false;
            }
            catch (Exception ex)
            {
                problems++;
                Log(EventTypes.Error, "Could not remove the device installation restrictions. " + ex.Message, "", "");
            }

            foreach (LedgerEntry entry in ledger.Entries)
            {
                DeviceOpResult op = backend.Unblock(entry.InstanceId);
                if (op.Ok)
                {
                    ledger.Remove(entry.InstanceId);
                    if (!entry.Restart && !entry.Premarked)
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
                    bool applied = readOnly.IsApplied();
                    if (applied) readOnly.Remove();
                    ledger.ReadOnlyOwned = false;
                    Log(EventTypes.ReadOnlyOff, "Removable storage is writable again.", "", "");
                    // A drive that is connected keeps the setting it was connected
                    // under. Without this it would stay read-only until it is replugged.
                    if (applied) RestartStorage(backend.Scan(), clock());
                }
            }
            catch (Exception ex)
            {
                problems++;
                Log(EventTypes.Error, "Could not remove the read-only setting. " + ex.Message, "", "");
            }
            saveLedger(ledger);
            ReinstallRefused();
            return problems;
        }

        // ---------- strict mode ----------

        /// <summary>
        /// Keeps the Windows installation restrictions in step with the policy.
        /// While they are on, a USB device Windows has not installed before is
        /// refused, so it never starts. The devices the policy allows are named
        /// as exceptions.
        /// </summary>
        private void SyncGate(Policy policy, PassResult result, DateTime now)
        {
            // Developer mode and tests that do not care run without the Windows policy.
            if (gate == null || gate is NoInstallGate) return;
            try
            {
                List<string> deny = InstallRules.DenyFor(policy.Armed && policy.Strict,
                    policy.Storage == StorageMode.Block, policy.Pnp == PnpMode.Block);
                bool owned = gate.Owned;

                if (deny == null)
                {
                    gateOwned = false;
                    lastGateNote = null;
                    if (!owned) return;
                    gate.Remove();
                    Log(EventTypes.PolicyChanged, SystemWords.GateOpened, "", "");
                    ReinstallRefused();
                    return;
                }
                if (!owned && !gate.Supported)
                {
                    gateOwned = false;
                    NoteOnce(EventTypes.Warning, SystemWords.GateUnsupported);
                    return;
                }
                if (!owned && gate.Foreign)
                {
                    gateOwned = false;
                    NoteOnce(EventTypes.Warning, SystemWords.GateForeign);
                    return;
                }

                var allow = new List<string>();
                foreach (DeviceStatus status in result.Devices)
                {
                    if (status.Decision.Verdict != Verdict.Allow) continue;
                    if (ledger.Contains(status.Device.InstanceId)) continue;
                    allow.Add(status.Device.InstanceId);
                    foreach (UsbChild child in backend.Children(status.Device.InstanceId)) allow.Add(child.InstanceId);
                }
                // A device trusted by its own ID is let in wherever it is plugged
                // in, without the moment it takes LOTUS to open the door.
                foreach (TrustEntry entry in policy.Trusted)
                {
                    if (entry.Kind == TrustKind.Device) allow.Add(entry.Id);
                }
                // What was let through a moment ago may be between two states right now.
                foreach (string id in new List<string>(admitted.Keys))
                {
                    if (now - admitted[id] > AdmitGrace)
                    {
                        admitted.Remove(id);
                        lettingIn.Remove(id);
                    }
                    else allow.Add(id);
                }

                var plan = new InstallPlan(deny, allow);
                if (!gate.Matches(plan))
                {
                    if (!owned)
                    {
                        foreach (DeviceStatus status in result.Devices)
                        {
                            if (status.Device.InstallRefused) notInstalledBefore.Add(status.Device.InstanceId);
                        }
                    }
                    gate.Apply(plan);
                    if (!owned)
                    {
                        Log(EventTypes.PolicyChanged, SystemWords.GateClosed, "", "");
                    }
                }
                gateOwned = true;
                lastGateNote = null;
            }
            catch (Exception ex)
            {
                NoteOnce(EventTypes.Error, SystemWords.GateFailed + ex.Message);
            }
        }

        private void NoteOnce(string type, string message)
        {
            if (lastGateNote == message) return;
            lastGateNote = message;
            Log(type, message, "", "");
        }

        /// <summary>The policy allows this device. If Windows refused to install it or one of its functions, let it in.</summary>
        private void Admit(UsbDevice device, DateTime now)
        {
            if (!gateOwned) return;
            if (device.Started) lettingIn.Remove(device.InstanceId);
            if (device.InstallRefused)
            {
                LetIn(device.InstanceId, device.Name, now);
                return; // its functions only show up once the device itself runs
            }
            foreach (UsbChild child in backend.Children(device.InstanceId))
            {
                if (child.Started) lettingIn.Remove(child.InstanceId);
                else if (child.InstallRefused) LetIn(child.InstanceId, device.Name, now);
            }
        }

        /// <summary>One function of a device arrived. Let it in when the device it belongs to is allowed.</summary>
        private void AdmitFunction(Policy policy, string instanceId, DateTime now)
        {
            if (!gateOwned) return;
            string parentId = backend.ParentOf(instanceId);
            if (parentId == null || ledger.Contains(parentId)) return;
            UsbDevice parent = backend.ScanOne(parentId);
            if (parent == null || !parent.Started) return;
            TimeSpan seenFor = firstSeen.TryGetValue(parentId, out DateTime first) ? now - first : TimeSpan.Zero;
            Decision decision = PolicyEngine.Evaluate(policy, parent, seenFor);
            if (decision.Verdict != Verdict.Allow) return;
            foreach (UsbChild child in backend.Children(parentId))
            {
                if (string.Equals(child.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase) &&
                    child.InstallRefused)
                {
                    LetIn(child.InstanceId, parent.Name, now);
                }
            }
        }

        /// <summary>
        /// Lets a device through that the policy allows and Windows refused. The
        /// device is named as an exception at once. If Windows had already decided
        /// against it by then, it is made to forget the device and find it again a
        /// moment later, because Windows only decides when it meets a device.
        /// </summary>
        private void LetIn(string instanceId, string name, DateTime now)
        {
            if (notInstalledBefore.Contains(instanceId)) return;

            if (!lettingIn.TryGetValue(instanceId, out LetInState state))
            {
                state = new LetInState { AllowedAt = now };
                lettingIn[instanceId] = state;
                admitted[instanceId] = now;
                gate.AllowInstance(instanceId);
                if (backend is IAdmitsAtOnce)
                {
                    // Nothing else is working on this device, so switch it on now.
                    state.Attempts = 1;
                    state.ReinstalledAt = now;
                    backend.Reinstall(instanceId);
                }
                pendingNow++;
                return;
            }

            DateTime last = state.ReinstalledAt ?? state.AllowedAt;
            TimeSpan wait = state.ReinstalledAt == null ? InstallSettle : AdmitSettle;
            if (now - last < wait)
            {
                pendingNow++;
                return;
            }

            if (state.Attempts >= MaxAdmitAttempts)
            {
                if (!state.Reported)
                {
                    state.Reported = true;
                    Log(EventTypes.Warning, SystemWords.NotLetIn(name), instanceId, name);
                }
                return;
            }

            state.Attempts++;
            state.ReinstalledAt = now;
            admitted[instanceId] = now;
            gate.AllowInstance(instanceId);
            backend.Reinstall(instanceId);
            pendingNow++;
        }

        /// <summary>
        /// The restrictions are gone. Devices Windows refused while they were on
        /// stay refused until Windows meets them again, so make it.
        /// </summary>
        private void ReinstallRefused()
        {
            try
            {
                foreach (UsbDevice device in backend.Scan())
                {
                    if (ledger.Contains(device.InstanceId)) continue;
                    if (device.InstallRefused)
                    {
                        backend.Reinstall(device.InstanceId);
                        continue;
                    }
                    foreach (UsbChild child in backend.Children(device.InstanceId))
                    {
                        if (child.InstallRefused) backend.Reinstall(child.InstanceId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(EventTypes.Warning, SystemWords.RefusedRetryFailed + ex.Message +
                                        " Unplug them and plug them in again.", "", "");
            }
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
