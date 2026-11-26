using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Threading;

namespace Lotus.Core
{
    /// <summary>What the service loop needs from its surroundings. Tests swap parts of it out.</summary>
    public sealed class GuardOptions
    {
        public IDeviceBackend Backend;
        public IReadOnlyPolicy ReadOnly;
        public string PipeName = GuardProtocol.PipeName;
        /// <summary>Only elevated administrators may use the control pipe. Off in tests.</summary>
        public bool AdministratorsOnly = true;
        /// <summary>Set owner and permissions on the data folder. Off in tests, which do not run as SYSTEM.</summary>
        public bool HardenFolders = true;
        /// <summary>Ask Windows to report device changes. Off in tests, which drive the loop by hand.</summary>
        public bool WatchDevices = true;
        /// <summary>How much work a new password hash costs. Tests lower it to stay fast.</summary>
        public int PasswordIterations = PasswordRecord.DefaultIterations;
    }

    /// <summary>
    /// The loop behind the LotusGuard service. It decides about a device the
    /// instant Windows reports it, runs a full enforcement pass every two seconds
    /// and after every change, owns the sealed policy store, and answers the
    /// control pipe through which the app asks for policy changes.
    /// </summary>
    public sealed class GuardWorker : IDisposable
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(40);
        // A device that could not be switched off is tried again four times a second
        // for the first ten seconds, instead of waiting for the next regular pass.
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);
        private const int QuickRetries = 40;
        private const int FreeAttempts = 5;

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
        private DeviceWatcher watcher;
        private FileSystemWatcher fileWatcher;
        private GuardPipeServer pipe;
        private int fullSoon;

        private SecureStore store;
        private StoreContent content = new StoreContent();
        private Policy policy = new Policy();
        private bool lockdown;
        private Enforcer enforcer;
        private bool markAbsentDue = true;
        private int wrongPasswords;
        private DateTime lockedUntil = DateTime.MinValue;
        private string stopPurpose;
        private string lastPassError;
        private string lastSystemDiskError;
        private string lastStatus;

        /// <summary>
        /// Raised after a stop that came in over the control pipe with the right
        /// password. The service host reacts by stopping the service.
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
                PrepareFolders();
                LoadStore();

                Ledger ledger = LoadLedger();
                IDeviceBackend backend = options.Backend ?? new WindowsDeviceBackend();
                enforcer = new Enforcer(backend, options.ReadOnly ?? new RegistryReadOnlyPolicy(), ledger, SaveLedger,
                    log.Write);

                bool notifications = true;
                if (options.WatchDevices)
                {
                    watcher = new DeviceWatcher(OnDeviceChange);
                    notifications = watcher.Start();
                }
                StartFileWatcher();
                pipe = new GuardPipeServer(options.PipeName, Dispatch,
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
            if (usb && (action == DeviceWatcher.Enumerated || action == DeviceWatcher.Removed))
            {
                // The same ID can come back as a different device, so read it afresh.
                DeviceScanner.Forget(instanceId);
            }
            if (usb && (action == DeviceWatcher.Enumerated || action == DeviceWatcher.Started))
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
                clean = result.Failures == 0;
                lastPassError = null;

                string diskError = (backend as WindowsDeviceBackend)?.SystemDiskError;
                if (diskError != lastSystemDiskError)
                {
                    lastSystemDiskError = diskError;
                    if (diskError != null)
                    {
                        log.Write(EventTypes.Warning,
                            "Could not work out which disk Windows runs from, so storage devices are left alone for now. " +
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

            // Setup has normally done all of this already. The service repeats it,
            // because it is the one that trusts what these folders contain.
            try
            {
                if (Directory.Exists(LotusPaths.DataDir) && FolderGuard.IsReparsePoint(LotusPaths.DataDir))
                {
                    Directory.Delete(LotusPaths.DataDir, false);
                }
                if (!FolderGuard.CreateNew(LotusPaths.DataDir, FolderGuard.PublicFolderRules()))
                {
                    FolderGuard.Apply(LotusPaths.DataDir, FolderGuard.PublicFolderRules(), "secure");
                }

                if (Directory.Exists(LotusPaths.SecureDir) && FolderGuard.IsReparsePoint(LotusPaths.SecureDir))
                {
                    Directory.Delete(LotusPaths.SecureDir, false);
                }
                if (!FolderGuard.CreateNew(LotusPaths.SecureDir, FolderGuard.SecretFolderRules()))
                {
                    FolderGuard.Apply(LotusPaths.SecureDir, FolderGuard.SecretFolderRules(), "seal.key");
                }
                string key = Path.Combine(LotusPaths.SecureDir, "seal.key");
                if (File.Exists(key) && !FolderGuard.IsReparsePoint(key))
                {
                    File.SetAccessControl(key, FolderGuard.KeyFileRules());
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException)
            {
                Directory.CreateDirectory(LotusPaths.DataDir);
                Directory.CreateDirectory(LotusPaths.SecureDir);
                log.Write(EventTypes.Error, "The permissions on the LOTUS data folder could not be set. " + ex.Message);
            }
        }

        // ---------- the policy store ----------

        private void LoadStore()
        {
            store = new SecureStore(LotusPaths.SecureDir, LotusPaths.LegacyPolicyFile);
            if (options.HardenFolders) store.KeyFileSecurity = FolderGuard.KeyFileRules;
            StoreLoad loaded = store.Load();
            content = loaded.Content;

            switch (loaded.State)
            {
                case StoreState.Fresh:
                    SaveStore();
                    break;
                case StoreState.Legacy:
                    if (loaded.Problem != null)
                    {
                        log.Write(EventTypes.Error, "The policy file from the earlier version could not be read. " +
                                                    loaded.Problem);
                    }
                    if (SaveStore())
                    {
                        DeleteQuietly(LotusPaths.LegacyPolicyFile, PolicyStore.BackupPath(LotusPaths.LegacyPolicyFile),
                            LotusPaths.LegacyPolicyFile + ".tmp");
                        log.Write(EventTypes.PolicyChanged,
                            "The policy from the earlier version was moved into the sealed policy store.");
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
            log.Write(EventTypes.PolicyChanged, "Policy loaded: " + Summarize(policy));
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
            string path = LotusPaths.LedgerFile;
            string legacy = LotusPaths.LegacyLedgerFile;
            bool fromLegacy = !File.Exists(path) && !File.Exists(path + ".bak") &&
                              (File.Exists(legacy) || File.Exists(legacy + ".bak"));
            Ledger ledger = Ledger.Load(fromLegacy ? legacy : path, out string error);
            if (error != null) log.Write(EventTypes.Error, "The block ledger could not be read. " + error);
            if (fromLegacy)
            {
                SaveLedger(ledger);
                if (File.Exists(path)) DeleteQuietly(legacy, legacy + ".bak", legacy + ".tmp");
            }
            return ledger;
        }

        private void SaveLedger(Ledger ledger)
        {
            try
            {
                ledger.Save(LotusPaths.LedgerFile);
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
                    ["storage"] = Policy.StorageToText(policy.Storage),
                    ["pnp"] = policy.Pnp == PnpMode.Block ? "block" : "allow",
                    ["hasPassword"] = content.Password != null,
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
            string password = Json.GetString(request, "password", "");
            switch (op)
            {
                case GuardProtocol.OpStatus:
                    return Accept();
                case GuardProtocol.OpVerify:
                    return Authorize(password) ?? Accept();
                case GuardProtocol.OpApply:
                    return ApplyPolicy(request, password);
                case GuardProtocol.OpSetPassword:
                    return ChangePassword(password, Json.GetString(request, "newPassword", ""));
                case GuardProtocol.OpStop:
                    return StopForSetup(password, Json.GetString(request, "purpose", ""));
                default:
                    return Refuse(GuardProtocol.ErrBadRequest, "LOTUS Guard does not know the request \"" +
                                                               TextSafety.Clean(op, 40) + "\".");
            }
        }

        private Dictionary<string, object> Accept(string message = "")
        {
            int retry = lockedUntil > DateTime.UtcNow ? (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalSeconds) : 0;
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["code"] = "",
                ["message"] = message,
                ["version"] = LotusPaths.Version,
                ["armed"] = policy.Armed,
                ["hasPassword"] = content.Password != null,
                ["lockdown"] = lockdown,
                ["retryAfter"] = retry
            };
        }

        private Dictionary<string, object> Refuse(string code, string message)
        {
            Dictionary<string, object> reply = Accept(message);
            reply["ok"] = false;
            reply["code"] = code;
            return reply;
        }

        /// <summary>Checks the password. Returns null when it is right, or the refusal to send back.</summary>
        private Dictionary<string, object> Authorize(string password)
        {
            if (content.Password == null) return null;

            DateTime now = DateTime.UtcNow;
            if (now < lockedUntil)
            {
                int seconds = (int)Math.Ceiling((lockedUntil - now).TotalSeconds);
                return Refuse(GuardProtocol.ErrLockedOut,
                    "Too many wrong passwords. Try again in " + seconds + (seconds == 1 ? " second." : " seconds."));
            }
            if (string.IsNullOrEmpty(password))
            {
                return Refuse(GuardProtocol.ErrPasswordNeeded, "Enter the LOTUS password.");
            }
            if (content.Password.Verify(password))
            {
                wrongPasswords = 0;
                return null;
            }

            wrongPasswords++;
            log.Write(EventTypes.Security, "A wrong LOTUS password was entered.");
            if (wrongPasswords >= FreeAttempts)
            {
                // 30 seconds, then a minute, then two, up to a quarter of an hour.
                double seconds = Math.Min(900, 30 * Math.Pow(2, wrongPasswords - FreeAttempts));
                lockedUntil = DateTime.UtcNow.AddSeconds(seconds);
                log.Write(EventTypes.Security, "Too many wrong passwords. Password entry is paused for " +
                                               (int)seconds + " seconds.");
            }
            return Refuse(GuardProtocol.ErrWrongPassword, "That is not the LOTUS password.");
        }

        private Dictionary<string, object> RefuseLockdown()
        {
            return Refuse(GuardProtocol.ErrLockdown,
                "LOTUS is locked down because its policy store was damaged. Run LOTUS-Setup again to reset it.");
        }

        private Dictionary<string, object> ApplyPolicy(Dictionary<string, object> request, string password)
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

            Dictionary<string, object> refusal = Authorize(password);
            if (refusal != null) return refusal;

            PasswordRecord record = content.Password;
            string newPassword = Json.GetString(request, "newPassword", "");
            if (record == null)
            {
                // An armed policy without a password would protect nothing.
                if (next.Armed && newPassword.Length == 0)
                {
                    return Refuse(GuardProtocol.ErrNewPasswordNeeded,
                        "Choose a LOTUS password before arming. It is needed for every later change.");
                }
                if (newPassword.Length > 0)
                {
                    string problem = PasswordRecord.Problem(newPassword);
                    if (problem != null) return Refuse(GuardProtocol.ErrWeakPassword, problem);
                    record = PasswordRecord.Create(newPassword, options.PasswordIterations);
                }
            }

            var updated = new StoreContent { Policy = next, Password = record, Serial = content.Serial };
            StoreContent previous = content;
            content = updated;
            if (!SaveStore())
            {
                content = previous;
                return Refuse(GuardProtocol.ErrFailed, "The policy could not be saved. See the activity log.");
            }

            if (previous.Password == null && record != null)
            {
                log.Write(EventTypes.Security, "A LOTUS password was set.");
            }
            policy = next;
            markAbsentDue = true;
            Interlocked.Exchange(ref fullSoon, 1);
            log.Write(EventTypes.PolicyChanged, "Policy changed: " + Summarize(policy));
            WriteStatus();
            return Accept();
        }

        private Dictionary<string, object> ChangePassword(string password, string newPassword)
        {
            if (lockdown) return RefuseLockdown();
            Dictionary<string, object> refusal = Authorize(password);
            if (refusal != null) return refusal;

            string problem = PasswordRecord.Problem(newPassword);
            if (problem != null) return Refuse(GuardProtocol.ErrWeakPassword, problem);

            bool had = content.Password != null;
            StoreContent previous = content;
            content = new StoreContent
            {
                Policy = content.Policy,
                Password = PasswordRecord.Create(newPassword, options.PasswordIterations),
                Serial = content.Serial
            };
            if (!SaveStore())
            {
                content = previous;
                return Refuse(GuardProtocol.ErrFailed, "The password could not be saved. See the activity log.");
            }
            log.Write(EventTypes.Security, had ? "The LOTUS password was changed." : "A LOTUS password was set.");
            WriteStatus();
            return Accept();
        }

        private Dictionary<string, object> StopForSetup(string password, string purpose)
        {
            if (purpose != GuardProtocol.PurposeUpdate && purpose != GuardProtocol.PurposeUninstall)
            {
                return Refuse(GuardProtocol.ErrBadRequest, "The stop request does not say what it is for.");
            }
            // In lockdown the password record is gone with the store. Removing or
            // reinstalling LOTUS is the only way out, so it is not held behind a
            // password nobody can check any more.
            if (!lockdown)
            {
                Dictionary<string, object> refusal = Authorize(password);
                if (refusal != null) return refusal;
            }

            if (purpose == GuardProtocol.PurposeUninstall)
            {
                log.Write(EventTypes.Security, "LOTUS is being removed. Every blocked device is switched back on.");
                int problems = enforcer.ReleaseEverything();
                store.Erase();
                DeleteQuietly(LotusPaths.LedgerFile, LotusPaths.LedgerFile + ".bak", LotusPaths.LedgerFile + ".tmp",
                    LotusPaths.StatusFile);
                if (options.HardenFolders) LetAdministratorsDelete();
                stopPurpose = purpose;
                Dictionary<string, object> reply = Accept(problems == 0
                    ? ""
                    : problems + " blocked device(s) could not be switched back on. Enable them in Device Manager.");
                reply["problems"] = problems;
                return reply;
            }

            log.Write(EventTypes.Security, "LOTUS Guard is stopping so that LOTUS can be updated.");
            stopPurpose = purpose;
            return Accept();
        }

        /// <summary>Hands the data folder back to administrators, so the uninstaller can delete it.</summary>
        private void LetAdministratorsDelete()
        {
            try
            {
                var open = new DirectorySecurity();
                open.SetAccessRuleProtection(true, false);
                const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                open.AddAccessRule(new FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                open.AddAccessRule(new FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                FolderGuard.Apply(LotusPaths.DataDir, open);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException)
            {
            }
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
            pipe?.Dispose();
            kick.Dispose();
            stop.Dispose();
        }
    }
}
