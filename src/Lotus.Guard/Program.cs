using System;
using System.Collections.Generic;
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
                    return Scan();
                case "--release-all":
                    return ReleaseAll();
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
            Console.WriteLine("  LotusGuard --release-all   Switch every blocked device back on and lift read-only mode.");
            Console.WriteLine("                             Asks for the LOTUS password when one is set.");
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

        private static int Scan()
        {
            StoreContent stored = SecureStore.ReadUnverified(LotusPaths.StoreFile);
            string source = stored != null ? "Store" : "Default";
            Policy policy = stored?.Policy;
            if (policy == null)
            {
                // Before the service has started for the first time after an update
                // from LOTUS 1.0, the old policy file is still the one that counts.
                PolicyLoadResult legacy = PolicyStore.Load(LotusPaths.LegacyPolicyFile);
                policy = legacy.Policy;
                if (legacy.Source != PolicySource.Default) source = "Legacy";
            }

            Ledger ledger = Ledger.Load(LotusPaths.LedgerFile, out string _);
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
                ["policy"] = GuardWorker.Summarize(policy),
                ["policySource"] = source,
                ["hasPassword"] = stored?.Password != null,
                ["ledgerEntries"] = ledger.Count,
                ["systemDiskError"] = backend.SystemDiskError,
                ["devices"] = list
            }));
            return 0;
        }

        private static int ReleaseAll()
        {
            // This switches every blocked device back on, so it is held behind the
            // same password as the policy.
            StoreContent stored = SecureStore.ReadUnverified(LotusPaths.StoreFile);
            if (stored?.Password != null)
            {
                string password = Environment.GetEnvironmentVariable("LOTUS_PASSWORD");
                if (string.IsNullOrEmpty(password)) password = ReadPassword("LOTUS password: ");
                if (!stored.Password.Verify(password))
                {
                    new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile)
                        .Write(EventTypes.Security, "A wrong LOTUS password was entered for --release-all.");
                    Console.WriteLine("That is not the LOTUS password. Nothing was changed.");
                    return 3;
                }
            }

            var log = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile);
            Ledger ledger = Ledger.Load(LotusPaths.LedgerFile, out string error);
            if (error != null) Console.WriteLine("Warning: " + error);
            var enforcer = new Enforcer(new WindowsDeviceBackend(), new RegistryReadOnlyPolicy(), ledger,
                l => l.Save(LotusPaths.LedgerFile), log.Write);
            int count = ledger.Count;
            int problems = enforcer.ReleaseEverything();
            Console.WriteLine(problems == 0
                ? "Released " + count + " blocked device(s). Removable storage is writable."
                : problems + " item(s) could not be released. See the activity log.");
            return problems == 0 ? 0 : 1;
        }

        private static string ReadPassword(string prompt)
        {
            Console.Write(prompt);
            if (Console.IsInputRedirected) return Console.ReadLine() ?? "";

            var text = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (text.Length > 0) text.Length--;
                    continue;
                }
                if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
            }
            Console.WriteLine();
            return text.ToString();
        }
    }

    internal sealed class GuardService : ServiceBase
    {
        private GuardWorker worker;

        public GuardService()
        {
            ServiceName = LotusPaths.ServiceName;
            // Windows cannot be asked to stop this service. It stops when setup asks
            // for it over the control pipe with the LOTUS password, and when Windows
            // shuts down.
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
