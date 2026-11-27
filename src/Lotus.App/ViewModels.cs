using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Lotus.App.Backend;
using Lotus.Core;

namespace Lotus.App
{
    public abstract class Notifier : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Raise([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected void RaiseAll()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>One line in the device list.</summary>
    public sealed class DeviceRow : Notifier
    {
        public string InstanceId { get; private set; }
        public UsbDevice Device { get; private set; }
        public DeviceStatus Status { get; private set; }

        public string Name { get; private set; }
        public string Detail { get; private set; }
        public string Glyph { get; private set; }
        public string KindText { get; private set; }
        public string StateText { get; private set; }
        /// <summary>allowed, trusted, blocked, busy or off. Picks the colour.</summary>
        public string StateKey { get; private set; }
        public string Why { get; private set; }
        public string SerialNote { get; private set; }
        public bool HasSerialNote => !string.IsNullOrEmpty(SerialNote);
        public bool IsTrusted { get; private set; }
        public bool CanTrust { get; private set; }
        public bool ShowReadOnly { get; private set; }
        public bool ShowBuiltIn { get; private set; }

        private string signature;

        public DeviceRow(string instanceId)
        {
            InstanceId = instanceId;
        }

        public void Update(DeviceStatus status, Policy policy, bool readOnlyActive, GuardState guard)
        {
            UsbDevice d = status.Device;
            Device = d;
            Status = status;

            string name = string.IsNullOrWhiteSpace(d.Name) ? "Unknown USB device" : d.Name;
            string vendor = string.IsNullOrWhiteSpace(d.Vendor) ? "" : d.Vendor + "   ";
            string serial = d.HasSerial ? "serial " + d.Serial : "no serial number";
            string detail = vendor + d.Vid + ":" + d.Pid + "   " + serial;
            DeviceKinds kinds = d.FunctionsKnown && d.Kinds != DeviceKinds.None ? d.Kinds : DeviceKinds.Other;

            string stateText, stateKey, why = PolicyEngine.Explain(status.Decision, policy);
            switch (status.State)
            {
                case DeviceState.Trusted:
                    stateText = "TRUSTED"; stateKey = "trusted";
                    break;
                case DeviceState.Blocked:
                    stateText = "BLOCKED"; stateKey = "blocked";
                    if (!status.Decision.IsBlock) why = "The policy allows this device again. It is being switched back on.";
                    break;
                case DeviceState.Blocking:
                    stateText = "BLOCKING"; stateKey = "busy";
                    why = guard == GuardState.Running || guard == GuardState.Simulated
                        ? "The policy blocks this device. LOTUS Guard is switching it off."
                        : "The policy blocks this device, but LOTUS Guard is not running, so it still works.";
                    break;
                case DeviceState.OffElsewhere:
                    stateText = "OFF"; stateKey = "off";
                    why = "Switched off outside LOTUS, for example in Device Manager. LOTUS leaves it as it is.";
                    break;
                case DeviceState.Identifying:
                    stateText = "CHECKING"; stateKey = "busy";
                    break;
                default:
                    stateText = "ALLOWED"; stateKey = "allowed";
                    break;
            }

            bool trusted = policy.IsTrusted(d);
            bool readOnly = readOnlyActive && d.HasStorage && !d.IsOff;
            string note = d.HasSerial
                ? ""
                : "This device reports no serial number. Trusting the device only holds for the port it is in now. Trust the model to let it work in any port.";

            string sig = string.Join("|", name, detail, kinds, stateText, stateKey, why, trusted, readOnly,
                d.BuiltIn, d.IsSystemDisk);
            if (sig == signature) return;
            signature = sig;

            Name = name;
            Detail = detail;
            KindText = string.Join(" + ", UsbClassifier.Labels(kinds)).ToUpperInvariant();
            Glyph = GlyphFor(kinds);
            StateText = stateText;
            StateKey = stateKey;
            Why = why;
            SerialNote = note;
            IsTrusted = trusted;
            CanTrust = !trusted && !d.IsSystemDisk;
            ShowReadOnly = readOnly;
            ShowBuiltIn = d.BuiltIn;
            RaiseAll();
        }

        private static string GlyphFor(DeviceKinds kinds)
        {
            if ((kinds & DeviceKinds.Storage) != 0) return "";   // USB
            if ((kinds & DeviceKinds.Portable) != 0) return "";  // phone
            if ((kinds & DeviceKinds.Hub) != 0) return "";       // devices
            if ((kinds & DeviceKinds.Bluetooth) != 0) return ""; // bluetooth
            if ((kinds & DeviceKinds.Network) != 0) return "";   // ethernet
            if ((kinds & DeviceKinds.Audio) != 0) return "";     // headphone
            if ((kinds & DeviceKinds.Video) != 0) return "";     // camera
            if ((kinds & DeviceKinds.Printer) != 0) return "";   // print
            if ((kinds & DeviceKinds.Input) != 0) return "";     // keyboard
            return "";
        }
    }

    /// <summary>One line in the trusted list.</summary>
    public sealed class TrustRow
    {
        public TrustKind Kind { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string KindText { get; set; }
        public string Added { get; set; }
        public bool Connected { get; set; }
        public string ConnectedText => Connected ? "CONNECTED" : "NOT CONNECTED";
        /// <summary>When the trust ends, as it is shown and typed: 2026-12-04 17:00. Empty for trust without an end.</summary>
        public string Until { get; set; }
        public string TermLabel => string.IsNullOrEmpty(Until) ? "NO END" : "ENDS";
    }

    /// <summary>One line in the activity list.</summary>
    public sealed class EventRow
    {
        public string Time { get; set; }
        public string TypeText { get; set; }
        /// <summary>stop, ok, warn, info or quiet. Picks the colour.</summary>
        public string TypeKey { get; set; }
        public string Message { get; set; }

        public static EventRow From(LotusEvent e)
        {
            var row = new EventRow
            {
                Time = e.TimeUtc == DateTime.MinValue
                    ? ""
                    : e.TimeUtc.ToLocalTime().ToString("dd MMM  HH:mm:ss", CultureInfo.InvariantCulture),
                Message = e.Message
            };
            switch (e.Type)
            {
                case EventTypes.Blocked: row.TypeText = "BLOCKED"; row.TypeKey = "stop"; break;
                case EventTypes.Released: row.TypeText = "ALLOWED"; row.TypeKey = "ok"; break;
                case EventTypes.Connected: row.TypeText = "PLUGGED IN"; row.TypeKey = "quiet"; break;
                case EventTypes.Removed: row.TypeText = "UNPLUGGED"; row.TypeKey = "quiet"; break;
                case EventTypes.PolicyChanged: row.TypeText = "POLICY"; row.TypeKey = "info"; break;
                case EventTypes.TrustEnded: row.TypeText = "TRUST ENDED"; row.TypeKey = "warn"; break;
                case EventTypes.ReadOnlyOn:
                case EventTypes.ReadOnlyOff: row.TypeText = "READ-ONLY"; row.TypeKey = "info"; break;
                case EventTypes.GuardStarted:
                case EventTypes.GuardStopped: row.TypeText = "GUARD"; row.TypeKey = "quiet"; break;
                case EventTypes.Error: row.TypeText = "ERROR"; row.TypeKey = "warn"; break;
                case EventTypes.Tamper: row.TypeText = "TAMPERING"; row.TypeKey = "stop"; break;
                case EventTypes.Security: row.TypeText = "SETUP"; row.TypeKey = "info"; break;
                default: row.TypeText = "WARNING"; row.TypeKey = "warn"; break;
            }
            return row;
        }
    }

    /// <summary>What a confirmation dialog shows.</summary>
    public sealed class ConfirmRequest
    {
        public string Title = "";
        public string Intro = "";
        public string OffCaption = "";
        public List<string> Off = new List<string>();
        public string UntrustedCaption = "";
        public List<string> Untrusted = new List<string>();
        public string CheckText = "";
        public bool Checked;
        public string OnCaption = "";
        public List<string> On = new List<string>();
        public string Warning = "";
        public string ConfirmText = "APPLY";
        public bool Danger;
    }

    /// <summary>A policy change that is ready to be confirmed and saved.</summary>
    public sealed class ApplyPlan
    {
        public Policy Next;
        public List<UsbDevice> TrustCandidates = new List<UsbDevice>();
        public ConfirmRequest Confirm;
    }

    public sealed class MainViewModel : Notifier
    {
        private readonly IAppBackend backend;
        private Policy live = new Policy();
        private Policy draft = new Policy();
        private List<DeviceStatus> lastDevices = new List<DeviceStatus>();
        private GuardState guard;
        private bool readOnlyActive;
        private bool loaded;
        private bool lockdown;
        private string strictState = "";
        private string policyError;
        private string eventsSignature = "";
        private string trustSignature = "";

        public ObservableCollection<DeviceRow> Devices { get; } = new ObservableCollection<DeviceRow>();
        public ObservableCollection<TrustRow> Trusted { get; } = new ObservableCollection<TrustRow>();
        public ObservableCollection<EventRow> Events { get; } = new ObservableCollection<EventRow>();
        /// <summary>The newest few events, shown on the control page.</summary>
        public ObservableCollection<EventRow> Recent { get; } = new ObservableCollection<EventRow>();

        public MainViewModel(IAppBackend backend)
        {
            this.backend = backend;
        }

        public bool IsDemo => backend.IsDemo;
        public string Version => "v" + LotusPaths.Version;

        // ---------- live state ----------

        public bool IsArmed => live.Armed;
        public string HeroTitle => live.Armed ? "ARMED" : "DISARMED";

        public string HeroText
        {
            get
            {
                if (!loaded) return "Reading the policy.";
                if (!live.Armed)
                {
                    return "Nothing is blocked. Choose your settings below, switch LOTUS on and press Apply.";
                }
                string storage = live.Storage == StorageMode.Block ? "blocked"
                    : live.Storage == StorageMode.ReadOnly ? "read-only"
                    : "allowed";
                string pnp = live.Pnp == PnpMode.Block ? "trusted only" : "allowed";
                return "Removable storage: " + storage + ".  Other USB devices: " + pnp + ".";
            }
        }

        public int ConnectedCount => lastDevices.Count;
        public int BlockedCount => lastDevices.Count(s => s.State == DeviceState.Blocked || s.State == DeviceState.Blocking);
        public int TrustedCount => live.Trusted.Count;

        public string GuardText
        {
            get
            {
                switch (guard)
                {
                    case GuardState.Running: return "GUARD ONLINE";
                    case GuardState.Simulated: return "DEMO GUARD";
                    case GuardState.Stopped: return "GUARD STOPPED";
                    default: return "GUARD MISSING";
                }
            }
        }

        public bool GuardOk => guard == GuardState.Running || guard == GuardState.Simulated;
        public bool GuardStopped => loaded && guard == GuardState.Stopped;
        public bool GuardMissing => loaded && guard == GuardState.NotInstalled;
        public bool HasPolicyError => lockdown || !string.IsNullOrEmpty(policyError);
        public string PolicyErrorText => lockdown
            ? "LOTUS is locked down. Its policy store was damaged or removed, so removable storage and " +
              "untrusted USB devices are blocked. Run LOTUS-Setup again to reset it."
            : policyError;

        public bool NoDevices => loaded && Devices.Count == 0;
        public bool NoTrusted => loaded && Trusted.Count == 0;
        public bool NoEvents => loaded && Events.Count == 0;

        // ---------- draft settings ----------

        public bool DraftArmed
        {
            get => draft.Armed;
            set { if (draft.Armed != value) { draft.Armed = value; RaiseDraft(); } }
        }

        public bool StorageAllow
        {
            get => draft.Storage == StorageMode.Allow;
            set { if (value) SetStorage(StorageMode.Allow); }
        }

        public bool StorageReadOnly
        {
            get => draft.Storage == StorageMode.ReadOnly;
            set { if (value) SetStorage(StorageMode.ReadOnly); }
        }

        public bool StorageBlock
        {
            get => draft.Storage == StorageMode.Block;
            set { if (value) SetStorage(StorageMode.Block); }
        }

        public bool PnpAllow
        {
            get => draft.Pnp == PnpMode.Allow;
            set { if (value) SetPnp(PnpMode.Allow); }
        }

        public bool PnpBlock
        {
            get => draft.Pnp == PnpMode.Block;
            set { if (value) SetPnp(PnpMode.Block); }
        }

        public bool DraftAllowInput
        {
            get => draft.AllowInput;
            set { if (draft.AllowInput != value) { draft.AllowInput = value; RaiseDraft(); } }
        }

        public bool DraftStrict
        {
            get => draft.Strict;
            set { if (draft.Strict != value) { draft.Strict = value; RaiseDraft(); } }
        }

        /// <summary>There is nothing to refuse unless something is blocked.</summary>
        public bool StrictOptionEnabled => draft.Storage == StorageMode.Block || draft.Pnp == PnpMode.Block;

        public string StrictHint
        {
            get
            {
                if (!StrictOptionEnabled) return "Applies when storage is blocked or devices are trusted only.";
                if (!draft.Strict)
                {
                    return "Off: a blocked device this PC has never seen runs for a few milliseconds before LOTUS has it switched off.";
                }
                if (live.Armed && live.Strict && strictState == "foreign")
                {
                    return "This PC already has device installation restrictions that LOTUS did not set, so LOTUS cannot add its own. Blocked devices are switched off straight after they start instead.";
                }
                if (live.Armed && live.Strict && strictState == "unsupported")
                {
                    return "This version of Windows cannot do this. Blocked devices are switched off straight after they start instead.";
                }
                return "Windows does not install a blocked device it has never seen, so it never starts. A new device the policy allows comes up a fraction of a second later.";
            }
        }

        private void SetStorage(StorageMode mode)
        {
            if (draft.Storage == mode) return;
            draft.Storage = mode;
            RaiseDraft();
        }

        private void SetPnp(PnpMode mode)
        {
            if (draft.Pnp == mode) return;
            draft.Pnp = mode;
            RaiseDraft();
        }

        public bool HasChanges => loaded && !draft.SettingsEqual(live);

        public string DraftArmedText => draft.Armed ? "ON" : "OFF";

        public string StorageHint
        {
            get
            {
                switch (draft.Storage)
                {
                    case StorageMode.ReadOnly:
                        return "Files can be opened and copied from a drive, but nothing can be saved to one. " +
                               "This applies to every removable drive, trusted or not.";
                    case StorageMode.Block:
                        return "Drives, card readers and phones are switched off as soon as they are plugged in. " +
                               "Devices on the trusted list keep working.";
                    default:
                        return "USB drives, external disks, card readers and phones work normally.";
                }
            }
        }

        public string PnpHint
        {
            get
            {
                if (draft.Pnp == PnpMode.Allow)
                {
                    return "Any keyboard, headset, adapter or other USB device works when plugged in.";
                }
                return draft.AllowInput
                    ? "A USB device only works if it is on the trusted list. Keyboards and mice are the exception."
                    : "A USB device only works if it is on the trusted list. That includes keyboards and mice.";
            }
        }

        public bool InputOptionEnabled => draft.Pnp == PnpMode.Block;

        public string InputHint => draft.AllowInput
            ? "Any keyboard or mouse works, so you can always reach this window."
            : "Stops keyboards that type on their own. Trust your own keyboard and mouse before you apply.";

        public string PendingText => draft.Armed || live.Armed
            ? "You have changes that are not applied yet."
            : "Your settings are saved once you press Apply. Switch LOTUS on to enforce them.";

        private void RaiseDraft()
        {
            Raise(nameof(DraftArmed));
            Raise(nameof(DraftArmedText));
            Raise(nameof(StorageAllow));
            Raise(nameof(StorageReadOnly));
            Raise(nameof(StorageBlock));
            Raise(nameof(PnpAllow));
            Raise(nameof(PnpBlock));
            Raise(nameof(DraftAllowInput));
            Raise(nameof(DraftStrict));
            Raise(nameof(StrictOptionEnabled));
            Raise(nameof(StrictHint));
            Raise(nameof(StorageHint));
            Raise(nameof(PnpHint));
            Raise(nameof(InputHint));
            Raise(nameof(InputOptionEnabled));
            Raise(nameof(HasChanges));
            Raise(nameof(PendingText));
        }

        public void Discard()
        {
            CopySettings(live, draft);
            RaiseDraft();
        }

        private static void CopySettings(Policy from, Policy to)
        {
            to.Armed = from.Armed;
            to.Storage = from.Storage;
            to.Pnp = from.Pnp;
            to.AllowInput = from.AllowInput;
            to.Strict = from.Strict;
        }

        // ---------- snapshot ----------

        public void ApplySnapshot(AppSnapshot snapshot)
        {
            bool hadChanges = HasChanges;
            bool settingsMoved = !loaded || !snapshot.Policy.SettingsEqual(live);
            live = snapshot.Policy;
            guard = snapshot.Guard;
            readOnlyActive = snapshot.ReadOnlyActive;
            policyError = snapshot.PolicyError;
            lockdown = snapshot.Lockdown;
            strictState = snapshot.StrictState ?? "";
            lastDevices = snapshot.Devices;

            // Follow the saved policy unless the user is in the middle of editing.
            if (!loaded || (settingsMoved && !hadChanges))
            {
                CopySettings(live, draft);
            }
            loaded = true;

            SyncDevices(snapshot);
            SyncTrusted();
            SyncEvents(snapshot.Events);

            Raise(nameof(IsArmed));
            Raise(nameof(HeroTitle));
            Raise(nameof(HeroText));
            Raise(nameof(ConnectedCount));
            Raise(nameof(BlockedCount));
            Raise(nameof(TrustedCount));
            Raise(nameof(GuardText));
            Raise(nameof(GuardOk));
            Raise(nameof(GuardStopped));
            Raise(nameof(GuardMissing));
            Raise(nameof(HasPolicyError));
            Raise(nameof(PolicyErrorText));
            Raise(nameof(StrictHint));
            Raise(nameof(NoDevices));
            Raise(nameof(NoTrusted));
            Raise(nameof(NoEvents));
            RaiseDraft();
        }

        private void SyncDevices(AppSnapshot snapshot)
        {
            // Blocked devices first, then by name, so the list does not jump around.
            List<DeviceStatus> ordered = snapshot.Devices
                .OrderBy(s => s.State == DeviceState.Blocked || s.State == DeviceState.Blocking ? 0 : 1)
                .ThenBy(s => s.Device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.Device.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = Devices.Count - 1; i >= 0; i--)
            {
                string id = Devices[i].InstanceId;
                if (!ordered.Any(s => s.Device.InstanceId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    Devices.RemoveAt(i);
                }
            }

            for (int i = 0; i < ordered.Count; i++)
            {
                DeviceStatus status = ordered[i];
                int at = -1;
                for (int j = 0; j < Devices.Count; j++)
                {
                    if (Devices[j].InstanceId.Equals(status.Device.InstanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        at = j;
                        break;
                    }
                }
                DeviceRow row;
                if (at < 0)
                {
                    row = new DeviceRow(status.Device.InstanceId);
                    Devices.Insert(Math.Min(i, Devices.Count), row);
                }
                else
                {
                    row = Devices[at];
                    if (at != i && i < Devices.Count) Devices.Move(at, i);
                }
                row.Update(status, live, readOnlyActive, guard);
            }
        }

        private void SyncTrusted()
        {
            var rows = new List<TrustRow>();
            foreach (TrustEntry entry in live.Trusted)
            {
                rows.Add(new TrustRow
                {
                    Kind = entry.Kind,
                    Id = entry.Id,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed device" : entry.Name,
                    KindText = entry.Kind == TrustKind.Vendor ? "WHOLE VENDOR"
                        : entry.Kind == TrustKind.Model ? "WHOLE MODEL" : "THIS DEVICE",
                    Added = "added " + entry.AddedUtc.ToLocalTime().ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                    Connected = lastDevices.Any(s => entry.Matches(s.Device)),
                    Until = entry.UntilText()
                });
            }
            string signature = string.Join("|", rows.Select(r => r.Kind + r.Id + r.Name + r.Connected + r.Until));
            if (signature == trustSignature) return;
            trustSignature = signature;
            Trusted.Clear();
            foreach (TrustRow row in rows) Trusted.Add(row);
        }

        private void SyncEvents(List<LotusEvent> events)
        {
            string signature = events.Count + "|" + (events.Count > 0
                ? Json.FormatTime(events[0].TimeUtc) + events[0].Type + events[0].Message
                : "");
            if (signature == eventsSignature) return;
            eventsSignature = signature;
            Events.Clear();
            Recent.Clear();
            foreach (LotusEvent e in events)
            {
                EventRow row = EventRow.From(e);
                Events.Add(row);
                if (Recent.Count < 5) Recent.Add(row);
            }
        }

        // ---------- actions ----------

        /// <summary>Works out what applying the draft would do to the devices connected right now.</summary>
        public ApplyPlan PlanApply()
        {
            Policy next = live.Clone();
            CopySettings(draft, next);

            var plan = new ApplyPlan { Next = next };
            var confirm = new ConfirmRequest { Title = "APPLY POLICY", ConfirmText = "APPLY" };

            if (!next.Armed)
            {
                confirm.Intro = live.Armed
                    ? "LOTUS will be disarmed. Nothing is blocked while it is off, and every device it blocked is switched back on. Your settings are kept."
                    : "Your settings are saved, but LOTUS stays off, so nothing is blocked.";
            }
            else
            {
                string storage = next.Storage == StorageMode.Block ? "blocked"
                    : next.Storage == StorageMode.ReadOnly ? "read-only"
                    : "allowed";
                string pnp = next.Pnp == PnpMode.Block ? "trusted only" : "allowed";
                confirm.Intro = "LOTUS will be armed.  Removable storage: " + storage + ".  Other USB devices: " +
                                pnp + ".";
            }

            foreach (DeviceStatus status in lastDevices)
            {
                UsbDevice device = status.Device;
                if (status.State == DeviceState.OffElsewhere) continue;
                Decision decision = PolicyEngine.Evaluate(next, device, TimeSpan.MaxValue);
                bool working = !device.IsOff;
                if (decision.IsBlock && working)
                {
                    if (decision.Reason == Reason.PnpBlocked)
                    {
                        plan.TrustCandidates.Add(device);
                        confirm.Untrusted.Add(device.Name);
                    }
                    else
                    {
                        confirm.Off.Add(device.Name);
                    }
                }
                else if (!decision.IsBlock && status.State == DeviceState.Blocked)
                {
                    confirm.On.Add(device.Name);
                }
            }

            confirm.OffCaption = "Removable storage that is switched off right away:";
            confirm.UntrustedCaption = "Connected USB devices that are not trusted yet:";
            confirm.OnCaption = "Switched back on:";
            if (plan.TrustCandidates.Count > 0)
            {
                confirm.CheckText = plan.TrustCandidates.Count == 1
                    ? "Trust this device, so it keeps working"
                    : "Trust these " + plan.TrustCandidates.Count + " devices, so they keep working";
                confirm.Checked = true;
            }
            if (next.Armed && next.Pnp == PnpMode.Block && !next.AllowInput)
            {
                confirm.Warning = "Keyboards and mice are not exempt. One that is not trusted stops working the moment you apply.";
            }
            else if (next.Armed && next.Storage == StorageMode.ReadOnly && live.Storage != StorageMode.ReadOnly)
            {
                confirm.Warning = "Connected drives are reconnected for a moment so the change reaches them. Close files on them first.";
            }
            confirm.Danger = confirm.Off.Count > 0 || plan.TrustCandidates.Count > 0;
            plan.Confirm = confirm;
            return plan;
        }

        /// <summary>The policy a confirmed plan leads to. Nothing is saved yet.</summary>
        public Policy Finish(ApplyPlan plan, bool trustCandidates)
        {
            if (trustCandidates)
            {
                foreach (UsbDevice device in plan.TrustCandidates)
                {
                    // A device without a serial number gets a new identity in every
                    // port, so trust its model instead.
                    plan.Next.AddTrust(device.HasSerial ? TrustEntry.ForDevice(device) : TrustEntry.ForModel(device));
                }
            }
            return plan.Next;
        }

        /// <summary>The current policy with one more trusted device. Nothing is saved yet.</summary>
        public Policy WithTrust(DeviceRow row, TrustKind kind)
        {
            if (row?.Device == null) return null;
            Policy next = live.Clone();
            next.AddTrust(kind == TrustKind.Model ? TrustEntry.ForModel(row.Device) : TrustEntry.ForDevice(row.Device));
            return next;
        }

        /// <summary>
        /// The current policy with an entry someone typed in. Null when the list
        /// has that entry already. Nothing is saved yet.
        /// </summary>
        public Policy WithCustomTrust(TrustEntry entry, bool named)
        {
            if (entry == null) return null;
            if (entry.Kind == TrustKind.Device)
            {
                // A device that is plugged in is known, so what it does is recorded with the entry.
                DeviceStatus seen = lastDevices.FirstOrDefault(s => s.Device.InstanceId == entry.Id);
                if (seen != null)
                {
                    TrustEntry recorded = TrustEntry.ForDevice(seen.Device);
                    if (named) recorded.Name = entry.Name;
                    entry = recorded;
                }
            }
            Policy next = live.Clone();
            return next.AddTrust(entry) ? next : null;
        }

        /// <summary>
        /// The current policy with a new end for one trust entry, or with the end
        /// taken away. Null when nothing would change. Nothing is saved yet.
        /// </summary>
        public Policy WithTerm(TrustKind kind, string id, DateTime? untilUtc)
        {
            Policy next = live.Clone();
            TrustEntry entry = next.Trusted.FirstOrDefault(e => e.Kind == kind && e.Id == id);
            if (entry == null || entry.UntilUtc == untilUtc) return null;
            entry.UntilUtc = untilUtc;
            return next;
        }

        /// <summary>How to give trust an end, shown above the list.</summary>
        public const string TermHint =
            "To limit an entry, type how long it should last into its field and press Enter: 12h, 7d, 2w, or a date such as 2026-12-04. Clear the field for no end.";

        /// <summary>What to type for each kind of entry, shown under the field.</summary>
        public static string CustomTrustHint(TrustKind kind)
        {
            switch (kind)
            {
                case TrustKind.Vendor:
                    return "A vendor ID, for example 046D. Every device that vendor makes will work, also ones you have never seen.";
                case TrustKind.Model:
                    return "A vendor ID and a product ID, for example 046D:C52B. Every unit of that model will work.";
                default:
                    return "An instance path, for example USB\\VID_0781&PID_5567\\4C530001230505116353. That one device will work.";
            }
        }

        /// <summary>
        /// Prepares removing trust. Returns a confirmation when that would switch a
        /// connected device off, or null when it can go ahead silently.
        /// </summary>
        public ConfirmRequest PlanUntrust(TrustKind kind, string id, out Policy next)
        {
            next = live.Clone();
            next.RemoveTrust(kind, id);

            var off = new List<string>();
            foreach (DeviceStatus status in lastDevices)
            {
                if (status.Device.IsOff) continue;
                if (PolicyEngine.Evaluate(next, status.Device, TimeSpan.MaxValue).IsBlock) off.Add(status.Device.Name);
            }
            if (off.Count == 0) return null;
            return new ConfirmRequest
            {
                Title = "REMOVE TRUST",
                Intro = "The policy blocks this device once it is no longer trusted.",
                OffCaption = "Switched off right away:",
                Off = off,
                ConfirmText = "REMOVE",
                Danger = true
            };
        }

        public IEnumerable<TrustEntry> TrustEntriesFor(DeviceRow row)
        {
            return row?.Device == null
                ? Enumerable.Empty<TrustEntry>()
                : live.Trusted.Where(e => e.Matches(row.Device)).ToList();
        }

        /// <summary>Called after LOTUS Guard accepted a policy, so the window shows it without waiting.</summary>
        public void Accepted(Policy next, bool followSettings)
        {
            live = next.Clone();
            if (followSettings) CopySettings(next, draft);
            RaiseDraft();
        }
    }
}
