using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using Lotus.Core;

namespace Lotus.App.Backend
{
    public enum GuardState
    {
        Running,
        Stopped,
        NotInstalled,
        /// <summary>Demo mode: a simulated guard runs inside the app.</summary>
        Simulated
    }

    /// <summary>Everything the window shows, read in one go.</summary>
    public sealed class AppSnapshot
    {
        public Policy Policy = new Policy();
        public string PolicyError;
        public List<DeviceStatus> Devices = new List<DeviceStatus>();
        public List<LotusEvent> Events = new List<LotusEvent>();
        public GuardState Guard;
        public bool ReadOnlyActive;
        /// <summary>The policy store was damaged and LOTUS blocks everything until it is reinstalled.</summary>
        public bool Lockdown;
    }

    /// <summary>What came of an attempt to save.</summary>
    public sealed class SaveResult
    {
        public bool Ok;
        /// <summary>One of the GuardProtocol.Err codes, or "unavailable".</summary>
        public string Code = "";
        public string Message = "";

        public static SaveResult From(GuardReply reply)
        {
            return new SaveResult { Ok = reply.Ok, Code = reply.Code, Message = reply.Message };
        }
    }

    /// <summary>Where the app gets its data and where it saves the policy.</summary>
    public interface IAppBackend
    {
        bool IsDemo { get; }
        /// <summary>Reads the current state. Called on a background thread.</summary>
        AppSnapshot Read();
        /// <summary>Asks LOTUS Guard to put a policy in force.</summary>
        SaveResult SavePolicy(Policy policy);
        /// <summary>Starts the guard service. Returns an error message, or null when it worked.</summary>
        string StartGuard();
        string LogFolder { get; }
    }

    /// <summary>The installed product: real devices and the LotusGuard service, which holds the policy.</summary>
    public sealed class RealBackend : IAppBackend
    {
        private readonly ActivityLog log = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile);
        private readonly WindowsDeviceBackend devices = new WindowsDeviceBackend();
        private readonly RegistryReadOnlyPolicy readOnly = new RegistryReadOnlyPolicy();
        private readonly GuardClient guard;

        public RealBackend() : this(new GuardClient())
        {
        }

        /// <param name="guard">Developers point this at a guard started with --console --dev.</param>
        public RealBackend(GuardClient guard)
        {
            this.guard = guard;
        }

        public bool IsDemo => false;
        public string LogFolder => LotusPaths.DataDir;

        public AppSnapshot Read()
        {
            var snapshot = new AppSnapshot();
            // The policy and the list of blocked devices are in an encrypted store
            // that only LOTUS Guard can open, so the app asks the guard for them.
            var ledger = new Ledger();
            try
            {
                GuardReply reply = guard.Read();
                if (reply.Ok)
                {
                    snapshot.Policy = reply.ReadPolicy() ?? new Policy();
                    ledger = reply.ReadLedger();
                }
                else
                {
                    snapshot.PolicyError = reply.Message;
                }
                snapshot.Lockdown = reply.Lockdown;
            }
            catch (GuardUnavailableException ex)
            {
                snapshot.PolicyError = "The policy cannot be shown. " + ex.Message;
                snapshot.Lockdown = PublicStatus.Read().Lockdown;
            }
            catch (FormatException ex)
            {
                snapshot.PolicyError = "LOTUS Guard sent a policy that could not be read. " + ex.Message;
            }
            if (snapshot.Lockdown) snapshot.Policy = GuardWorker.LockdownPolicy();

            foreach (UsbDevice device in devices.Scan())
            {
                snapshot.Devices.Add(DeviceView.Resolve(snapshot.Policy, device, ledger, TimeSpan.MaxValue));
            }

            snapshot.Events = log.ReadRecent(400);
            snapshot.Guard = ReadGuardState();
            try
            {
                snapshot.ReadOnlyActive = readOnly.IsApplied();
            }
            catch (Exception)
            {
                snapshot.ReadOnlyActive = false;
            }
            return snapshot;
        }

        private static GuardState ReadGuardState()
        {
            try
            {
                using (var controller = new ServiceController(LotusPaths.ServiceName))
                {
                    return controller.Status == ServiceControllerStatus.Running ||
                           controller.Status == ServiceControllerStatus.StartPending
                        ? GuardState.Running
                        : GuardState.Stopped;
                }
            }
            catch (InvalidOperationException)
            {
                return GuardState.NotInstalled;
            }
        }

        public SaveResult SavePolicy(Policy policy)
        {
            try
            {
                return SaveResult.From(guard.Apply(policy.ToJson()));
            }
            catch (GuardUnavailableException ex)
            {
                return new SaveResult { Code = "unavailable", Message = ex.Message };
            }
        }

        public string StartGuard()
        {
            try
            {
                using (var controller = new ServiceController(LotusPaths.ServiceName))
                {
                    if (controller.Status != ServiceControllerStatus.Running &&
                        controller.Status != ServiceControllerStatus.StartPending)
                    {
                        controller.Start();
                    }
                    controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                }
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return "The service did not start within 15 seconds.";
            }
        }
    }

    /// <summary>
    /// Demo mode. The devices are made up and the guard runs inside the app, so the
    /// whole interface can be tried without installing anything or touching real hardware.
    /// </summary>
    public sealed class DemoBackend : IAppBackend
    {
        private readonly object gate = new object();
        private readonly SimulatedBackend bus = new SimulatedBackend();
        private readonly SimulatedReadOnlyPolicy readOnly = new SimulatedReadOnlyPolicy();
        private readonly List<LotusEvent> events = new List<LotusEvent>();
        private readonly Enforcer enforcer;
        private Policy policy = new Policy();

        public const string StickId = @"USB\VID_0781&PID_5567\4C530001230505116353";
        public const string KeyboardId = @"USB\VID_1A2C&PID_2D23\5&CC3F949&0&6";

        public bool IsDemo => true;
        public string LogFolder => null;

        /// <summary>Raised when the simulated guard blocks something, so the demo can show a notification.</summary>
        public event Action<LotusEvent> Blocked;

        public DemoBackend()
        {
            bus.NotDisableable.Add(KeyboardId);
            bus.Plug(new UsbDevice
            {
                InstanceId = KeyboardId, Name = "USB Keyboard", Vendor = "", Vid = "1A2C", Pid = "2D23",
                IsComposite = true, Functions = new List<string> { "03" }, Kinds = DeviceKinds.Input
            });
            bus.Plug(new UsbDevice
            {
                InstanceId = @"USB\VID_046D&PID_C542\5&CC3F949&0&5", Name = "Wireless Receiver", Vendor = "Logitech",
                Vid = "046D", Pid = "C542", Functions = new List<string> { "03" }, Kinds = DeviceKinds.Input
            });
            bus.Plug(new UsbDevice
            {
                InstanceId = @"USB\VID_03F0&PID_0C9D\0000000000000000", Name = "HyperX Cloud III Wireless",
                Vendor = "HP", Vid = "03F0", Pid = "0C9D", Serial = "0000000000000000", HasSerial = true,
                IsComposite = true, Functions = new List<string> { "01", "03" },
                Kinds = DeviceKinds.Audio | DeviceKinds.Input
            });
            bus.Plug(new UsbDevice
            {
                InstanceId = @"USB\VID_04F2&PID_B6D9\0001", Name = "Integrated Camera", Vendor = "Chicony",
                Vid = "04F2", Pid = "B6D9", Serial = "0001", HasSerial = true, BuiltIn = true,
                Functions = new List<string> { "0E" }, Kinds = DeviceKinds.Video
            });
            bus.Plug(new UsbDevice
            {
                InstanceId = @"USB\VID_18D1&PID_4EE1\0A1B2C3D4E5F", Name = "Pixel 8", Vendor = "Google",
                Vid = "18D1", Pid = "4EE1", Serial = "0A1B2C3D4E5F", HasSerial = true,
                Functions = new List<string> { "MTP" }, Kinds = DeviceKinds.Portable
            });
            bus.Plug(new UsbDevice
            {
                InstanceId = @"USB\VID_2357&PID_0604\E848B8C82000", Name = "TP-Link Bluetooth USB Adapter",
                Vendor = "TP-Link", Vid = "2357", Pid = "0604", Serial = "E848B8C82000", HasSerial = true,
                Functions = new List<string> { "E0" }, Kinds = DeviceKinds.Bluetooth
            });

            enforcer = new Enforcer(bus, readOnly, new Ledger(), null, AddEvent);
            AddEvent(new LotusEvent
            {
                Type = EventTypes.GuardStarted,
                Message = "Demo mode started. These devices are simulated and nothing on this PC is touched."
            });
            enforcer.RunOnce(policy);
        }

        private void AddEvent(LotusEvent e)
        {
            lock (gate) events.Add(e);
            if (e.Type == EventTypes.Blocked) Blocked?.Invoke(e);
        }

        public bool StickPlugged => bus.IsPlugged(StickId);

        /// <summary>Plugs the pretend flash drive in, or pulls it out.</summary>
        public void ToggleStick()
        {
            if (bus.IsPlugged(StickId))
            {
                bus.Unplug(StickId);
                return;
            }
            bus.Plug(new UsbDevice
            {
                InstanceId = StickId, Name = "SanDisk Cruzer Blade USB Device", Vendor = "SanDisk", Vid = "0781",
                Pid = "5567", Serial = "4C530001230505116353", HasSerial = true,
                Functions = new List<string> { "08" }, Kinds = DeviceKinds.Storage
            });
        }

        public AppSnapshot Read()
        {
            lock (gate)
            {
                PassResult pass = enforcer.RunOnce(policy);
                var snapshot = new AppSnapshot
                {
                    Policy = policy.Clone(),
                    Devices = pass.Devices,
                    Guard = GuardState.Simulated,
                    ReadOnlyActive = readOnly.Applied
                };
                snapshot.Events = new List<LotusEvent>(events);
                snapshot.Events.Reverse();
                return snapshot;
            }
        }

        public SaveResult SavePolicy(Policy newPolicy)
        {
            lock (gate)
            {
                bool changed = !policy.SettingsEqual(newPolicy) || policy.Trusted.Count != newPolicy.Trusted.Count;
                policy = newPolicy.Clone();
                if (changed)
                {
                    events.Add(new LotusEvent
                    {
                        Type = EventTypes.PolicyChanged,
                        Message = "Policy changed: " + GuardWorker.Summarize(policy)
                    });
                }
                return new SaveResult { Ok = true };
            }
        }

        public string StartGuard()
        {
            return null;
        }
    }
}
