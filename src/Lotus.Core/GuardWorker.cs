using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Lotus.Core
{
    /// <summary>What the service loop needs from its surroundings. Tests swap parts of it out.</summary>
    public sealed class GuardOptions
    {
        /// <summary>The operating system underneath. Left empty, it is the one this copy was built for.</summary>
        public GuardPlatform Platform;
        public IDeviceBackend Backend;
        public IReadOnlyPolicy ReadOnly;
        /// <summary>The system's way of keeping new devices from starting. Tests and the developer mode put their own in.</summary>
        public IInstallGate InstallGate;
        public string PipeName = GuardProtocol.PipeName;
        /// <summary>Only elevated administrators may use the control pipe. Off in tests.</summary>
        public bool AdministratorsOnly = true;
        /// <summary>Set owner and permissions on the data folder. Off in tests, which do not run as SYSTEM.</summary>
        public bool HardenFolders = true;
        /// <summary>Ask Windows to report device changes. Off in tests, which drive the loop by hand.</summary>
        public bool WatchDevices = true;
        /// <summary>
        /// Asks whether setup wants a damaged policy store thrown away, and forgets
        /// the request. Tests put their own answer in.
        /// </summary>
        public Func<bool> TakeResetRequest;
    }

    /// <summary>
    /// The loop behind the LotusGuard service. It decides about a device the
    /// instant Windows reports it, runs a full enforcement pass every two seconds
    /// and after every change, owns the encrypted policy store, and answers the
    /// control pipe through which the app reads and changes the policy.
    /// </summary>
    public sealed class GuardWorker : IDisposable
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(40);
        // A device that could not be switched off is tried again four times a second
        // for the first ten seconds, instead of waiting for the next regular pass.
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);
        private const int QuickRetries = 40;

        private sealed class PendingCommand
        {
            public Dictionary<string, object> Request;
            public Dictionary<string, object> Reply;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private readonly GuardOptions options;
        private readonly AutoResetEvent kick = new AutoResetEvent(false);
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        private readonly ConcurrentQueue<string> arrivals = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<PendingCommand> commands = new ConcurrentQueue<PendingCommand>();
        private readonly ActivityLog log;
        private Thread thread;
        private GuardPlatform platform;
        private IDisposable watcher;
        private FileSystemWatcher fileWatcher;
        private IControlServer pipe;
        private int fullSoon;

        private SecureStore store;
        private StoreContent content = new StoreContent();
        private Policy policy = new Policy();
        private bool lockdown;
        private Enforcer enforcer;
        private IInstallGate installGate;
        private bool markAbsentDue = true;
        private string stopPurpose;
        private string lastPassError;
        private string lastSystemDiskError;
        private string lastStatus;

        /// <summary>
        /// Raised after setup asked over the control pipe for the service to stop.
        /// The service host reacts by stopping the service.
        /// </summary>
        public event Action<string> StopRequested;

        public GuardWorker(GuardOptions options = null)
        {
            this.options = options ?? new GuardOptions();
            log = new ActivityLog(LotusPaths.EventsFile, LotusPaths.OldEventsFile);
        }

        /// <summary>The policy every device is blocked under when the policy store cannot be trusted.</summary>
        public static Policy LockdownPolicy()
        {
            return new Policy { Armed = true, Storage = StorageMode.Block, Pnp = PnpMode.Block, AllowInput = true };
        }

        public void Start()
        {
            if (thread != null) return;
            stop.Reset();
            ready.Reset();
            thread = new Thread(Run) { IsBackground = true, Name = "LotusGuard" };
            thread.Start();
        }

        /// <summary>Waits until the loop has loaded its policy and opened the control pipe.</summary>
        public bool WaitUntilReady(int milliseconds)
        {
            return ready.Wait(milliseconds);
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

        /// <summary>Asks for a full enforcement pass right away.</summary>
        public void Kick()
        {
            Interlocked.Exchange(ref fullSoon, 1);
            kick.Set();
        }

        /// <summary>Tells the loop that Windows reported a device. The device is judged at once.</summary>
        public void NotifyArrival(string instanceId)
        {
            if (!string.IsNullOrEmpty(instanceId)) arrivals.Enqueue(instanceId);
            Kick();
        }

        // ---------- the loop ----------

        private void Run()
        {
            try
            {
                platform = options.Platform ?? GuardPlatform.Default();
                PrepareFolders();
                LoadStore();

                Ledger ledger = LoadLedger();
                IDeviceBackend backend = options.Backend ?? platform.CreateBackend();
                platform.LedgerLoaded(ledger);
                // Anything that is not the installed service keeps its hands off
                // the system's own policy: tests, and the guard developers start by hand.
                installGate = options.InstallGate ??
                              (options.HardenFolders ? platform.CreateInstallGate(backend) : new NoInstallGate());
                enforcer = new Enforcer(backend, options.ReadOnly ?? platform.CreateReadOnly(), ledger, SaveLedger,
                    log.Write, null, installGate);

                bool notifications = true;
                if (options.WatchDevices)
                {
                    watcher = platform.WatchDevices(OnDeviceChange);
                    notifications = watcher != null;
                }
                StartFileWatcher();
                pipe = platform.CreateControlServer(options.PipeName, Dispatch,
                    problem => log.Write(EventTypes.Tamper, problem), options.AdministratorsOnly);
                pipe.Start();

                log.Write(EventTypes.GuardStarted, "LOTUS Guard " + LotusPaths.Version + " started." +
                                                   (notifications ? "" : " Device notifications are unavailable, so it checks every two seconds instead."));
                WriteStatus();
                ready.Set();

                DateTime due = DateTime.UtcNow;
                int retries = 0;
                while (!stop.WaitOne(0))
                {
                    TimeSpan wait = due - DateTime.UtcNow;
                    if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
                    if (WaitHandle.WaitAny(new WaitHandle[] { stop, kick }, wait) == 0) break;

                    ProcessCommands();
                    if (stopPurpose != null) break;

                    DateTime soon = DateTime.UtcNow + Settle;
                    // Devices arrive in bursts. Each one is judged on its own right
                    // away, and one full pass follows when the burst is over.
                    if (ProcessArrivals() && soon < due) due = soon;
                    if (Interlocked.Exchange(ref fullSoon, 0) == 1 && soon < due) due = soon;
                    if (DateTime.UtcNow < due) continue;

                    bool clean = FullPass(backend);
                    retries = clean ? 0 : retries + 1;
                    due = DateTime.UtcNow + (!clean && retries <= QuickRetries ? RetryInterval : Interval);
                }
            }
            catch (Exception ex)
            {
                log.Write(EventTypes.Error, "LOTUS Guard stopped because of an unexpected problem. " + ex.Message);
                ready.Set();
                throw;
            }
            finally
            {
                fileWatcher?.Dispose();
                fileWatcher = null;
                watcher?.Dispose();
                watcher = null;
                // Answer whoever is still waiting, then close the pipe.
                while (commands.TryDequeue(out PendingCommand left))
                {
                    left.Reply = Refuse(GuardProtocol.ErrFailed, "LOTUS Guard is stopping.");
                    left.Done.Set();
                }
            }

            string purpose = stopPurpose;
            if (purpose != null)
            {
                // Give the pipe a moment to deliver the answer before the service goes away.
                Thread.Sleep(300);
            }
            pipe?.Dispose();
            pipe = null;
            log.Write(EventTypes.GuardStopped, purpose == GuardProtocol.PurposeUninstall
                ? "LOTUS Guard stopped because LOTUS is being removed."
                : "LOTUS Guard stopped. Devices that were blocked stay blocked.");
            if (purpose != null) StopRequested?.Invoke(purpose);
        }

        private void OnDeviceChange(int action, string instanceId)
        {
            bool usb = instanceId != null && instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase);
            if (usb && (action == DeviceChange.Enumerated || action == DeviceChange.Started))
            {
                NotifyArrival(instanceId);
                return;
            }
            Kick();
        }

        private bool ProcessArrivals()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (arrivals.TryDequeue(out string id))
            {
                if (!seen.Add(id)) continue;
                try
                {
                    enforcer.FastPass(policy, id);
                }
                catch (Exception ex)
                {
                    ReportPassError(ex);
                }
            }
            return seen.Count > 0;
        }

        /// <summary>Runs one full pass. Returns false when something could not be switched off or on.</summary>
        private bool FullPass(IDeviceBackend backend)
        {
            bool clean = true;
            try
            {
                CheckStore();
                if (markAbsentDue)
                {
                    markAbsentDue = false;
                    try
                    {
                        enforcer.MarkAbsent(policy);
                    }
                    catch (Exception ex)
                    {
                        log.Write(EventTypes.Warning,
                            "Could not mark unplugged devices as blocked ahead of time. " + ex.Message);
                    }
                }
                PassResult result = enforcer.RunOnce(policy);
                // A device that is being let in counts as unfinished business, so
                // that the next look comes in a quarter of a second, not in two.
                clean = result.Failures == 0 && result.Pending == 0;
                lastPassError = null;

                string diskError = (backend as IReportsSystemDisk)?.SystemDiskError;
                if (diskError != lastSystemDiskError)
                {
                    lastSystemDiskError = diskError;
                    if (diskError != null)
                    {
                        log.Write(EventTypes.Warning,
                            "Could not work out which disk the system runs from, so storage devices are left alone for now. " +
                            diskError);
                    }
                }
            }
            catch (Exception ex)
            {
                ReportPassError(ex);
            }
            return clean;
        }

        private void ReportPassError(Exception ex)
        {
            // Keep running. Report a repeating problem only once.
            if (lastPassError == ex.Message) return;
            lastPassError = ex.Message;
            log.Write(EventTypes.Error, "An enforcement pass failed. " + ex.Message);
        }

        // ---------- folders ----------

        private void PrepareFolders()
        {
            if (!options.HardenFolders)
            {
                Directory.CreateDirectory(LotusPaths.DataDir);
                Directory.CreateDirectory(LotusPaths.SecureDir);
                return;
            }

            platform.PrepareFolders(problem => log.Write(EventTypes.Error, problem));
        }

        // ---------- the policy store ----------

        private void LoadStore()
        {
            store = new SecureStore(LotusPaths.SecureDir, LotusPaths.LegacyPolicyFile);
            if (options.HardenFolders) platform.ConfigureStore(store);
            StoreLoad loaded = store.Load();

            // Only the installed service looks for a note from setup. Tests give their own answer.
            bool resetAsked = options.TakeResetRequest != null
                ? options.TakeResetRequest()
                : options.HardenFolders && platform.TakeResetRequest();
            if (loaded.State == StoreState.Tampered && resetAsked)
            {
                // Setup was run again on a locked-down PC. That is the way out:
                // the store that cannot be trusted goes, and LOTUS starts disarmed.
                log.Write(EventTypes.Tamper, "The policy store could not be trusted. " + loaded.Problem +
                                             " Setup asked for a reset, so LOTUS starts again disarmed.");
                store.ResetPolicy();
                loaded = new StoreLoad { State = StoreState.Fresh };
            }
            content = loaded.Content;

            switch (loaded.State)
            {
                case StoreState.Fresh:
                    SaveStore();
                    break;
                case StoreState.Legacy:
                    if (loaded.Problem != null)
                    {
                        log.Write(EventTypes.Error, "The policy from the earlier version could not be read in full. " +
                                                    loaded.Problem);
                    }
                    if (SaveStore())
                    {
                        DeleteQuietly(LotusPaths.LegacyPolicyFile, PolicyStore.BackupPath(LotusPaths.LegacyPolicyFile),
                            LotusPaths.LegacyPolicyFile + ".tmp");
                        log.Write(EventTypes.PolicyChanged,
                            "The policy from the earlier version was moved into the encrypted policy store.");
                    }
                    break;
                case StoreState.Restored:
                    log.Write(EventTypes.Tamper, "The policy store was damaged or missing. " + loaded.Problem +
                                                 " The last good copy is in use.");
                    SaveStore();
                    break;
                case StoreState.Tampered:
                    lockdown = true;
                    log.Write(EventTypes.Tamper, "The policy store cannot be trusted. " + loaded.Problem +
                                                 " LOTUS is locked down: removable storage and untrusted USB devices " +
                                                 "are blocked until LOTUS is installed again.");
                    break;
            }

            policy = lockdown ? LockdownPolicy() : content.Policy;
            log.Write(EventTypes.PolicyChanged, "Policy loaded: " + PublicSummary(policy));
        }

        private bool SaveStore()
        {
            try
            {
                store.Save(content);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.Cryptography.CryptographicException)
            {
                log.Write(EventTypes.Error, "The policy store could not be saved. " + ex.Message);
                return false;
            }
        }

        /// <summary>Puts the store back when someone other than LOTUS changed or removed it.</summary>
        private void CheckStore()
        {
            if (lockdown || store == null) return;
            if (store.IsUntouched(out string problem)) return;
            log.Write(EventTypes.Tamper, problem + " LOTUS put its own copy back. The policy did not change.");
            SaveStore();
        }

        private static void DeleteQuietly(params string[] paths)
        {
            foreach (string path in paths)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }

        private Ledger LoadLedger()
        {
            Ledger ledger = store.LoadLedger(out string error);
            if (error != null) log.Write(EventTypes.Error, "The list of blocked devices could not be read. " + error);
            if (File.Exists(store.LedgerPath) || File.Exists(store.LedgerPath + ".bak")) return ledger;

            // Nothing in the encrypted form yet. LOTUS 1.1 kept the list as a
            // readable file in the protected folder, and 1.0 in the open one.
            foreach (string old in new[] { store.OldLedgerPath, LotusPaths.LegacyLedgerFile })
            {
                if (!File.Exists(old) && !File.Exists(old + ".bak")) continue;
                ledger = Ledger.Load(old, out string oldError);
                if (oldError != null)
                {
                    log.Write(EventTypes.Error, "The list of blocked devices from the earlier version could not be read. " +
                                                oldError);
                }
                SaveLedger(ledger);
                if (File.Exists(store.LedgerPath)) DeleteQuietly(old, old + ".bak", old + ".tmp");
                break;
            }
            return ledger;
        }

        private void SaveLedger(Ledger ledger)
        {
            try
            {
                store.SaveLedger(ledger);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is System.Security.Cryptography.CryptographicException)
            {
                log.Write(EventTypes.Error, "The list of blocked devices could not be saved. " + ex.Message);
            }
        }

        private void StartFileWatcher()
        {
            try
            {
                fileWatcher = new FileSystemWatcher(LotusPaths.SecureDir)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size |
                                   NotifyFilters.CreationTime | NotifyFilters.Security
                };
                FileSystemEventHandler changed = (s, e) =>
                {
                    // The service's own ledger writes are not news.
                    if (e.Name != null && e.Name.StartsWith("ledger", StringComparison.OrdinalIgnoreCase)) return;
                    Kick();
                };
                fileWatcher.Changed += changed;
                fileWatcher.Created += changed;
                fileWatcher.Deleted += changed;
                fileWatcher.Renamed += (s, e) => Kick();
                fileWatcher.EnableRaisingEvents = true;
            }
            catch (Exception)
            {
                // Not fatal. The loop checks the store every two seconds anyway.
                fileWatcher = null;
            }
        }

        /// <summary>
        /// A small file anyone may read. The notifier, which has no rights of its
        /// own, uses it to show whether LOTUS is armed.
        /// </summary>
        private void WriteStatus()
        {
            try
            {
                string text = Json.Write(new Dictionary<string, object>
                {
                    ["version"] = LotusPaths.Version,
                    ["armed"] = policy.Armed,
                    ["lockdown"] = lockdown
                });
                if (text == lastStatus && File.Exists(LotusPaths.StatusFile)) return;
                AtomicFile.Write(LotusPaths.StatusFile, text, null);
                lastStatus = text;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        // ---------- the control pipe ----------

        /// <summary>Runs on the pipe thread: hands the request to the loop and waits for the answer.</summary>
        private Dictionary<string, object> Dispatch(Dictionary<string, object> request)
        {
            var command = new PendingCommand { Request = request };
            commands.Enqueue(command);
            kick.Set();
            return command.Done.Wait(TimeSpan.FromSeconds(110))
                ? command.Reply
                : Refuse(GuardProtocol.ErrFailed, "LOTUS Guard is busy. Try again in a moment.");
        }

        private void ProcessCommands()
        {
            while (stopPurpose == null && commands.TryDequeue(out PendingCommand command))
            {
                try
                {
                    command.Reply = Handle(command.Request);
                }
                catch (Exception ex)
                {
                    command.Reply = Refuse(GuardProtocol.ErrFailed, "LOTUS Guard could not do that. " + ex.Message);
                }
                command.Done.Set();
            }
        }

        private Dictionary<string, object> Handle(Dictionary<string, object> request)
        {
            string op = Json.GetString(request, "op", "");
            switch (op)
            {
                case GuardProtocol.OpStatus:
                    return Accept();
                case GuardProtocol.OpRead:
                    return ReadState();
                case GuardProtocol.OpApply:
                    return ApplyPolicy(request);
                case GuardProtocol.OpStop:
                    return StopForSetup(Json.GetString(request, "purpose", ""));
                default:
                    return Refuse(GuardProtocol.ErrBadRequest, "LOTUS Guard does not know the request \"" +
                                                               TextSafety.Clean(op, 40) + "\".");
            }
        }

        private Dictionary<string, object> Accept(string message = "")
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["code"] = "",
                ["message"] = message,
                ["version"] = LotusPaths.Version,
                ["armed"] = policy.Armed,
                ["lockdown"] = lockdown
            };
        }

        private Dictionary<string, object> Refuse(string code, string message)
        {
            Dictionary<string, object> reply = Accept(message);
            reply["ok"] = false;
            reply["code"] = code;
            return reply;
        }

        /// <summary>
        /// The policy in force and the list of blocked devices. The files they are
        /// kept in cannot be read by anyone but the service, so the app and the
        /// command line ask for them here.
        /// </summary>
        private Dictionary<string, object> ReadState()
        {
            Dictionary<string, object> reply = Accept();
            reply["policy"] = Json.Parse(policy.ToJson());
            reply["ledger"] = Json.Parse(enforcer.Ledger.ToJson());
            reply["strictState"] = StrictState();
            return reply;
        }

        private Dictionary<string, object> RefuseLockdown()
        {
            return Refuse(GuardProtocol.ErrLockdown,
                "LOTUS is locked down because its policy store was damaged. Run LOTUS-Setup again to reset it.");
        }

        /// <summary>"on", "off", or why Windows is not refusing new devices although the policy asks for it.</summary>
        private string StrictState()
        {
            try
            {
                if (installGate == null) return "off";
                if (installGate.Owned) return "on";
                if (!policy.Armed || !policy.Strict) return "off";
                if (!installGate.Supported) return "unsupported";
                return installGate.Foreign ? "foreign" : "off";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private Dictionary<string, object> ApplyPolicy(Dictionary<string, object> request)
        {
            if (lockdown) return RefuseLockdown();

            Policy next;
            try
            {
                Dictionary<string, object> raw =
                    Json.AsObject(request.TryGetValue("policy", out object value) ? value : null);
                if (raw == null) return Refuse(GuardProtocol.ErrBadRequest, "The request holds no policy.");
                next = Policy.FromJson(Json.Write(raw, false));
            }
            catch (FormatException ex)
            {
                return Refuse(GuardProtocol.ErrBadRequest, "The policy was not accepted. " + ex.Message);
            }

            var updated = new StoreContent { Policy = next, Serial = content.Serial };
            StoreContent previous = content;
            content = updated;
            if (!SaveStore())
            {
                content = previous;
                return Refuse(GuardProtocol.ErrFailed, "The policy could not be saved. See the activity log.");
            }

            policy = next;
            markAbsentDue = true;
            Interlocked.Exchange(ref fullSoon, 1);
            log.Write(EventTypes.PolicyChanged, "Policy changed: " + PublicSummary(policy));
            WriteStatus();
            return Accept();
        }

        private Dictionary<string, object> StopForSetup(string purpose)
        {
            if (purpose != GuardProtocol.PurposeUpdate && purpose != GuardProtocol.PurposeUninstall)
            {
                return Refuse(GuardProtocol.ErrBadRequest, "The stop request does not say what it is for.");
            }

            if (purpose == GuardProtocol.PurposeUninstall)
            {
                log.Write(EventTypes.Security, "LOTUS is being removed. Every blocked device is switched back on.");
                int problems = enforcer.ReleaseEverything();
                store.Erase();
                DeleteQuietly(LotusPaths.StatusFile);
                if (options.HardenFolders) platform.HandBackFolders();
                stopPurpose = purpose;
                Dictionary<string, object> reply = Accept(problems == 0
                    ? ""
                    : problems + " blocked device(s) could not be switched back on. " + SystemWords.SwitchOnByHand);
                reply["problems"] = problems;
                return reply;
            }

            log.Write(EventTypes.Security, "LOTUS Guard is stopping so that LOTUS can be updated.");
            stopPurpose = purpose;
            return Accept();
        }

        /// <summary>
        /// What the activity log says about a policy. Every user of the PC can
        /// read that log, so it only says what the status file says anyway. The
        /// settings and the trusted list stay in the encrypted store.
        /// </summary>
        public static string PublicSummary(Policy p)
        {
            return p.Armed ? "armed." : "disarmed, nothing is blocked.";
        }

        /// <summary>The policy in one sentence, for the app and the command line, which may see all of it.</summary>
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
            if (p.Strict && p.Pnp == PnpMode.Block) pnp += ", new devices refused before they start";
            else if (p.Strict && p.Storage == StorageMode.Block) pnp += ", new storage devices refused before they start";
            string trusted = p.Trusted.Count == 1 ? "1 trusted entry" : p.Trusted.Count + " trusted entries";
            return "armed, " + storage + ", " + pnp + ", " + trusted + ".";
        }

        public void Dispose()
        {
            Stop();
            pipe?.Dispose();
            kick.Dispose();
            stop.Dispose();
        }
    }
}
