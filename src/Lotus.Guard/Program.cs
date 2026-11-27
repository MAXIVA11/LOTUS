using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Lotus.Core;

namespace Lotus.Guard
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (command)
            {
                case "":
                    if (Environment.UserInteractive)
                    {
                        PrintUsage();
                        return 0;
                    }
                    ServiceBase.Run(new GuardService());
                    return 0;
                case "--console":
                    return RunConsole(args.Length >= 3 && args[1].ToLowerInvariant() == "--dev" ? args[2] : null);
                case "--scan":
                    return Scan(args.Length >= 2 && args[1].ToLowerInvariant() == "--dev");
                case "--trust-connected":
                    return TrustConnected(args.Length >= 2 && args[1].ToLowerInvariant() == "--dev");
                case "--release-all":
                    try
                    {
                        return ReleaseAll();
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        Console.WriteLine("Could not finish. " + ex.Message);
                        return 1;
                    }
                default:
                    PrintUsage();
                    return 2;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("LOTUS Guard " + LotusPaths.Version);
            Console.WriteLine("Windows starts this program as the LotusGuard service. You can also run it by hand:");
            Console.WriteLine();
            Console.WriteLine("  LotusGuard --scan          List USB devices and what the current policy says about each.");
            Console.WriteLine("  LotusGuard --console       Enforce the policy from this window until you press Enter.");
            Console.WriteLine("  LotusGuard --console --dev <folder>");
            Console.WriteLine("                             For developers: keep all data in <folder> and listen on a");
            Console.WriteLine("                             separate pipe. Leaves the installed LOTUS alone.");
            Console.WriteLine("  LotusGuard --trust-connected");
            Console.WriteLine("                             Put every USB device that is connected right now on the");
            Console.WriteLine("                             trusted list. Setup uses this when you ask it to.");
            Console.WriteLine("  LotusGuard --release-all   Disarm LOTUS, switch every blocked device back on and lift");
            Console.WriteLine("                             read-only mode. Works in Safe Mode too, where the service");
            Console.WriteLine("                             does not run. Needs an administrator command prompt.");
        }

        private static int RunConsole(string devFolder)
        {
            var options = new GuardOptions();
            if (devFolder != null)
            {
                // A second guard for trying things out. It has its own data folder and
                // its own pipe, so it cannot be mistaken for the service or disturb it.
                LotusPaths.OverrideDataDir(devFolder);
                options.PipeName = GuardProtocol.DevPipeName;
                options.AdministratorsOnly = false;
                options.HardenFolders = false;
            }
            using (var worker = new GuardWorker(options))
            using (var stopped = new ManualResetEvent(false))
            {
                worker.StopRequested += purpose => stopped.Set();
                worker.Start();
                Console.WriteLine("LOTUS Guard is enforcing the policy. Press Enter to stop.");
                if (Console.IsInputRedirected) stopped.WaitOne();
                else Console.ReadLine();
                worker.Stop();
            }
            return 0;
        }

        private static int Scan(bool dev)
        {
            // The policy and the list of blocked devices are in files only the
            // service can read, so the service is asked for them.
            Policy policy = new Policy();
            var ledger = new Ledger();
            string source = "Unknown";
            bool lockdown = false;
            string guard;
            try
            {
                GuardClient client = dev
                    ? new GuardClient(GuardProtocol.DevPipeName, false)
                    : new GuardClient();
                GuardReply reply = client.Read();
                guard = reply.Ok ? "answering" : "refused: " + reply.Message;
                if (reply.Ok)
                {
                    policy = reply.ReadPolicy() ?? new Policy();
                    ledger = reply.ReadLedger();
                    lockdown = reply.Lockdown;
                    source = "Service";
                }
            }
            catch (GuardUnavailableException ex)
            {
                guard = "not answering: " + ex.Message;
            }
            catch (FormatException ex)
            {
                guard = "sent something unreadable: " + ex.Message;
            }
            if (lockdown) policy = GuardWorker.LockdownPolicy();

            var backend = new WindowsDeviceBackend();
            var list = new List<object>();
            foreach (UsbDevice device in backend.Scan())
            {
                DeviceStatus status = DeviceView.Resolve(policy, device, ledger, TimeSpan.MaxValue);
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = device.InstanceId,
                    ["name"] = device.Name,
                    ["vendor"] = device.Vendor,
                    ["serial"] = device.Serial,
                    ["builtIn"] = device.BuiltIn,
                    ["kinds"] = string.Join(", ", UsbClassifier.Labels(device.Kinds)),
                    ["functions"] = string.Join(" ", device.Functions),
                    ["functionsKnown"] = device.FunctionsKnown,
                    ["described"] = device.Described,
                    ["started"] = device.Started,
                    ["problem"] = device.Problem,
                    ["canDisable"] = device.Disableable,
                    ["systemDisk"] = device.IsSystemDisk,
                    ["state"] = status.State.ToString(),
                    ["why"] = PolicyEngine.Explain(status.Decision, policy)
                });
            }
            Console.WriteLine(Json.Write(new Dictionary<string, object>
            {
                ["policy"] = source == "Unknown"
                    ? "unknown, because LOTUS Guard is not answering."
                    : GuardWorker.Summarize(policy),
                ["policySource"] = source,
                ["guard"] = guard,
                ["lockdown"] = lockdown,
                ["armed"] = policy.Armed,
                ["trusted"] = policy.Trusted.Count,
                ["ledgerEntries"] = ledger.Count,
                ["systemDiskError"] = backend.SystemDiskError,
                ["devices"] = list
            }));
            return 0;
        }

        /// <summary>
        /// The way out for someone who locked themselves out: LOTUS is disarmed
        /// and everything it switched off is switched back on.
        /// </summary>
        /// <summary>
        /// Puts every connected USB device on the trusted list. The service owns
        /// the policy, so it is asked for it and given the longer list back.
        /// </summary>
        private static int TrustConnected(bool dev)
        {
            GuardClient client = dev
                ? new GuardClient(GuardProtocol.DevPipeName, false)
                : new GuardClient();
            GuardReply state = null;
            // Right after installation the service may need a moment before it answers.
            for (int attempt = 0; attempt < 40 && state == null; attempt++)
            {
                try
                {
                    state = client.Read();
                }
                catch (GuardUnavailableException)
                {
                    Thread.Sleep(500);
                }
            }
            if (state == null)
            {
                Console.WriteLine("LOTUS Guard is not answering.");
                return 1;
            }
            if (!state.Ok)
            {
                Console.WriteLine(state.Lockdown
                    ? "LOTUS is locked down. Run LOTUS-Setup again to reset it."
                    : "LOTUS Guard refused. " + state.Message);
                return 1;
            }

            Policy policy;
            try
            {
                policy = state.ReadPolicy() ?? new Policy();
            }
            catch (FormatException ex)
            {
                Console.WriteLine("The policy could not be read. " + ex.Message);
                return 1;
            }
            List<TrustEntry> added = policy.TrustAll(DeviceScanner.Scan());
            if (added.Count == 0)
            {
                Console.WriteLine("Every connected USB device is on the trusted list already.");
                return 0;
            }
            GuardReply applied = client.Apply(policy.ToJson());
            if (!applied.Ok)
            {
                Console.WriteLine("LOTUS Guard refused. " + applied.Message);
                return 1;
            }
            foreach (TrustEntry entry in added) Console.WriteLine("Trusted: " + entry.Name);
            Console.WriteLine(added.Count == 1
                ? "One connected device was put on the trusted list."
                : added.Count + " connected devices were put on the trusted list.");
            return 0;
        }

        private static int ReleaseAll()
        {
            // With the service running, ask it. It owns the policy and the devices.
            try
            {
                var client = new GuardClient();
                GuardReply state = client.Read();
                if (state.Ok)
                {
                    Policy policy = state.ReadPolicy() ?? new Policy();
                    policy.Armed = false;
                    GuardReply applied = client.Apply(policy.ToJson());
                    if (!applied.Ok)
                    {
                        Console.WriteLine("LOTUS Guard refused. " + applied.Message);
                        return 1;
                    }
                    for (int attempt = 0; attempt < 60; attempt++)
                    {
                        if (client.Read().ReadLedger().Count == 0)
                        {
                            Console.WriteLine("LOTUS is disarmed. Every blocked device is switched back on.");
                            return 0;
                        }
                        Thread.Sleep(500);
                    }
                    Console.WriteLine("LOTUS is disarmed, but some devices are not back on yet. See the activity log.");
                    return 1;
                }
                if (state.Lockdown)
                {
                    Console.WriteLine("LOTUS is locked down. Run LOTUS-Setup again to reset it.");
                    return 1;
                }
            }
            catch (GuardUnavailableException)
            {
                // Not running: Safe Mode, or a stopped service. Do it by hand below.
            }
            catch (FormatException)
            {
            }
            return ReleaseWithoutService();
        }

        /// <summary>
        /// Without the service, the store has to be opened here. Only the service
        /// account may read it, so this takes the folder over first. That needs
        /// administrator rights, and the service puts the permissions back the
        /// next time it starts.
        /// </summary>
        private static int ReleaseWithoutService()
        {
            var log = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile);
            var store = new SecureStore(LotusPaths.SecureDir, LotusPaths.LegacyPolicyFile);
            if (Directory.Exists(LotusPaths.SecureDir) && !FolderGuard.OpenForAdministrators(LotusPaths.SecureDir))
            {
                Console.WriteLine("Could not open the LOTUS store. Run this from an administrator command prompt.");
                return 1;
            }

            StoreLoad loaded = store.Load();
            bool disarmed = false;
            if (loaded.State == StoreState.Ok || loaded.State == StoreState.Restored || loaded.State == StoreState.Legacy)
            {
                try
                {
                    loaded.Content.Policy.Armed = false;
                    store.Save(loaded.Content);
                    disarmed = true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                           ex is System.Security.Cryptography.CryptographicException)
                {
                    Console.WriteLine("Warning: the policy could not be saved as disarmed. " + ex.Message);
                }
            }

            Ledger ledger = store.LoadLedger(out string error);
            if (error != null) Console.WriteLine("Warning: " + error);
            if (ledger.Count == 0 && !File.Exists(store.LedgerPath))
            {
                // A PC that was updated and never ran the new service still has
                // its list where the earlier version kept it.
                foreach (string old in new[] { store.OldLedgerPath, LotusPaths.LegacyLedgerFile })
                {
                    if (!File.Exists(old) && !File.Exists(old + ".bak")) continue;
                    ledger = Ledger.Load(old, out string oldError);
                    if (oldError != null) Console.WriteLine("Warning: " + oldError);
                    break;
                }
            }

            var enforcer = new Enforcer(new WindowsDeviceBackend(), new RegistryReadOnlyPolicy(), ledger,
                l =>
                {
                    try
                    {
                        store.SaveLedger(l);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                               ex is System.Security.Cryptography.CryptographicException)
                    {
                        // The devices are back on all the same. The service tidies the list at its next start.
                    }
                }, log.Write, null, new RegistryInstallGate());
            int count = ledger.Count;
            int problems = enforcer.ReleaseEverything();
            Console.WriteLine(problems == 0
                ? "Released " + count + " blocked device(s). Removable storage is writable."
                : problems + " item(s) could not be released. See the activity log.");
            Console.WriteLine(disarmed
                ? "LOTUS is disarmed and stays that way until you arm it again in the app."
                : "The policy itself was not changed.");
            return problems == 0 ? 0 : 1;
        }
    }

    internal sealed class GuardService : ServiceBase
    {
        private GuardWorker worker;

        public GuardService()
        {
            ServiceName = LotusPaths.ServiceName;
            // Windows cannot be asked to stop this service. It stops when setup asks
            // for it over the control pipe, and when Windows shuts down.
            CanStop = false;
            CanShutdown = true;
            CanPauseAndContinue = false;
            // The activity log is the record. Writing to the Windows event log as
            // well would leave a registration behind after an uninstall.
            AutoLog = false;
        }

        protected override void OnStart(string[] args)
        {
            worker = new GuardWorker();
            worker.StopRequested += purpose => ThreadPool.QueueUserWorkItem(_ => Stop());
            worker.Start();
        }

        protected override void OnStop()
        {
            worker?.Dispose();
            worker = null;
        }

        protected override void OnShutdown()
        {
            worker?.Dispose();
            worker = null;
        }
    }
}
