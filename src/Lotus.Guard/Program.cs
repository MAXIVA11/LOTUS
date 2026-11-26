using System;
using System.Collections.Generic;
using System.ServiceProcess;
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
                    return RunConsole();
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
            Console.WriteLine("  LotusGuard --release-all   Switch every blocked device back on and lift read-only mode.");
        }

        private static int RunConsole()
        {
            using (var worker = new GuardWorker())
            {
                worker.Start();
                Console.WriteLine("LOTUS Guard is enforcing the policy. Press Enter to stop.");
                Console.ReadLine();
                worker.Stop();
            }
            return 0;
        }

        private static int Scan()
        {
            PolicyLoadResult loaded = PolicyStore.Load(LotusPaths.PolicyFile);
            Ledger ledger = Ledger.Load(LotusPaths.LedgerFile, out string _);
            var backend = new WindowsDeviceBackend();
            var list = new List<object>();
            foreach (UsbDevice device in backend.Scan())
            {
                DeviceStatus status = DeviceView.Resolve(loaded.Policy, device, ledger, TimeSpan.MaxValue);
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
                    ["started"] = device.Started,
                    ["problem"] = device.Problem,
                    ["canDisable"] = device.Disableable,
                    ["systemDisk"] = device.IsSystemDisk,
                    ["state"] = status.State.ToString(),
                    ["why"] = PolicyEngine.Explain(status.Decision, loaded.Policy)
                });
            }
            Console.WriteLine(Json.Write(new Dictionary<string, object>
            {
                ["policy"] = GuardWorker.Summarize(loaded.Policy),
                ["policySource"] = loaded.Source.ToString(),
                ["systemDiskError"] = backend.SystemDiskError,
                ["devices"] = list
            }));
            return 0;
        }

        private static int ReleaseAll()
        {
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
    }

    internal sealed class GuardService : ServiceBase
    {
        private GuardWorker worker;

        public GuardService()
        {
            ServiceName = LotusPaths.ServiceName;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            worker = new GuardWorker();
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
