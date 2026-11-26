using System;
using System.IO;
using System.Threading;

namespace Lotus.Core
{
    /// <summary>
    /// The loop behind the LotusGuard service. It wakes up when a device changes,
    /// when the policy file changes, and every two seconds regardless, and runs one
    /// enforcement pass each time.
    /// </summary>
    public sealed class GuardWorker : IDisposable
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

        private readonly AutoResetEvent kick = new AutoResetEvent(false);
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly ActivityLog log;
        private readonly string policyPath;
        private readonly string ledgerPath;
        private Thread thread;
        private DeviceWatcher watcher;
        private FileSystemWatcher fileWatcher;

        private Policy policy = new Policy();
        private bool policyLoaded;
        private DateTime policyStamp = DateTime.MinValue;
        private long policyLength = -1;
        private string lastPolicyError;
        private string lastPassError;
        private string lastSystemDiskError;

        public GuardWorker()
        {
            policyPath = LotusPaths.PolicyFile;
            ledgerPath = LotusPaths.LedgerFile;
            log = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile);
        }

        public void Start()
        {
            if (thread != null) return;
            stop.Reset();
            thread = new Thread(Run) { IsBackground = true, Name = "LotusGuard" };
            thread.Start();
        }

        public void Stop()
        {
            stop.Set();
            Thread t = thread;
            thread = null;
            if (t != null && t != Thread.CurrentThread) t.Join(TimeSpan.FromSeconds(20));
        }

        /// <summary>Runs the loop on the calling thread until Stop is called from elsewhere.</summary>
        public void RunBlocking()
        {
            stop.Reset();
            Run();
        }

        /// <summary>Asks for an enforcement pass right now.</summary>
        public void Kick()
        {
            kick.Set();
        }

        private void Run()
        {
            Directory.CreateDirectory(LotusPaths.DataDir);

            Ledger ledger = Ledger.Load(ledgerPath, out string ledgerError);
            if (ledgerError != null)
            {
                log.Write(EventTypes.Error, "The block ledger could not be read. " + ledgerError);
            }

            var backend = new WindowsDeviceBackend();
            var enforcer = new Enforcer(backend, new RegistryReadOnlyPolicy(), ledger, SaveLedger, log.Write);

            watcher = new DeviceWatcher(Kick);
            bool notifications = watcher.Start();
            StartFileWatcher();

            log.Write(EventTypes.GuardStarted, "LOTUS Guard " + LotusPaths.Version + " started." +
                                               (notifications ? "" : " Device notifications are unavailable, so it checks every two seconds instead."));

            while (!stop.WaitOne(0))
            {
                try
                {
                    ReloadPolicy();
                    enforcer.RunOnce(policy);
                    lastPassError = null;

                    if (backend.SystemDiskError != lastSystemDiskError)
                    {
                        lastSystemDiskError = backend.SystemDiskError;
                        if (lastSystemDiskError != null)
                        {
                            log.Write(EventTypes.Warning,
                                "Could not work out which disk Windows runs from, so storage devices are left alone for now. " +
                                lastSystemDiskError);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Keep running. Report a repeating problem only once.
                    if (lastPassError != ex.Message)
                    {
                        lastPassError = ex.Message;
                        log.Write(EventTypes.Error, "An enforcement pass failed. " + ex.Message);
                    }
                }

                int signaled = WaitHandle.WaitAny(new WaitHandle[] { stop, kick }, Interval);
                if (signaled == 0) break;
                if (signaled == 1)
                {
                    // Device changes arrive in bursts. Give the burst a moment to
                    // finish, then handle it in one pass.
                    if (stop.WaitOne(40)) break;
                    kick.Reset();
                }
            }

            fileWatcher?.Dispose();
            fileWatcher = null;
            watcher?.Dispose();
            watcher = null;
            log.Write(EventTypes.GuardStopped, "LOTUS Guard stopped. Devices that were blocked stay blocked.");
        }

        private void SaveLedger(Ledger ledger)
        {
            try
            {
                ledger.Save(ledgerPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                log.Write(EventTypes.Error, "The block ledger could not be saved. " + ex.Message);
            }
        }

        private void StartFileWatcher()
        {
            try
            {
                fileWatcher = new FileSystemWatcher(LotusPaths.DataDir, Path.GetFileName(policyPath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size |
                                   NotifyFilters.CreationTime
                };
                fileWatcher.Changed += (s, e) => Kick();
                fileWatcher.Created += (s, e) => Kick();
                fileWatcher.Renamed += (s, e) => Kick();
                fileWatcher.Deleted += (s, e) => Kick();
                fileWatcher.EnableRaisingEvents = true;
            }
            catch (Exception)
            {
                // Not fatal. The loop re-reads the file every two seconds anyway.
                fileWatcher = null;
            }
        }

        private void ReloadPolicy()
        {
            DateTime stamp = DateTime.MinValue;
            long length = -1;
            try
            {
                var info = new FileInfo(policyPath);
                if (info.Exists)
                {
                    stamp = info.LastWriteTimeUtc;
                    length = info.Length;
                }
            }
            catch (IOException)
            {
            }

            if (policyLoaded && stamp == policyStamp && length == policyLength) return;

            PolicyLoadResult loaded = PolicyStore.Load(policyPath);
            policyStamp = stamp;
            policyLength = length;

            if (loaded.Error != null)
            {
                if (loaded.Error != lastPolicyError)
                {
                    lastPolicyError = loaded.Error;
                    string consequence = loaded.Source == PolicySource.Backup
                        ? "The last good copy is in use."
                        : policyLoaded
                            ? "The policy that was already running stays in force."
                            : "LOTUS stays disarmed until the file is fixed.";
                    log.Write(EventTypes.Error, "The policy file could not be read. " + loaded.Error + " " + consequence);
                }
                // With no usable file at all, keep whatever was running.
                if (loaded.Source == PolicySource.Default && policyLoaded) return;
            }
            else
            {
                lastPolicyError = null;
            }

            bool changed = !policyLoaded || !policy.SettingsEqual(loaded.Policy) ||
                           policy.Trusted.Count != loaded.Policy.Trusted.Count;
            policy = loaded.Policy;
            policyLoaded = true;
            if (changed) log.Write(EventTypes.PolicyChanged, "Policy loaded: " + Summarize(policy));
        }

        public static string Summarize(Policy p)
        {
            if (!p.Armed) return "disarmed, nothing is blocked.";
            string storage = p.Storage == StorageMode.Block ? "removable storage blocked"
                : p.Storage == StorageMode.ReadOnly ? "removable storage read-only"
                : "removable storage allowed";
            string pnp = p.Pnp == PnpMode.Block
                ? "other USB devices blocked unless trusted" +
                  (p.AllowInput ? " (keyboards and mice stay allowed)" : "")
                : "other USB devices allowed";
            string trusted = p.Trusted.Count == 1 ? "1 trusted entry" : p.Trusted.Count + " trusted entries";
            return "armed, " + storage + ", " + pnp + ", " + trusted + ".";
        }

        public void Dispose()
        {
            Stop();
            kick.Dispose();
            stop.Dispose();
        }
    }
}
