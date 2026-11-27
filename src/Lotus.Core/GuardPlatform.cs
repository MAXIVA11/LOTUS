using System;
using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>The channel on which the app and setup talk to the service.</summary>
    public interface IControlServer : IDisposable
    {
        void Start();
    }

    /// <summary>What a report about a device change says happened.</summary>
    public static class DeviceChange
    {
        /// <summary>The system has just found the device.</summary>
        public const int Enumerated = 7;
        public const int Started = 8;
        public const int Removed = 9;
    }

    /// <summary>A backend that can say why it does not know which disk the system runs from.</summary>
    public interface IReportsSystemDisk
    {
        string SystemDiskError { get; }
    }

    /// <summary>
    /// A backend where a device the policy allows can be switched on in one
    /// step. On Windows the system first has to finish its own attempt to install
    /// the device, so LOTUS waits a moment. Where there is no such attempt,
    /// waiting would only keep an allowed device off for longer.
    /// </summary>
    public interface IAdmitsAtOnce
    {
    }

    /// <summary>
    /// Everything the service loop needs from the operating system it runs on.
    /// The loop itself, the policy engine, the enforcer and the encrypted store
    /// are the same on every system. This is the part that differs.
    /// </summary>
    public abstract class GuardPlatform
    {
        public abstract IDeviceBackend CreateBackend();

        public abstract IReadOnlyPolicy CreateReadOnly();

        /// <summary>The system's own way of keeping a new device from starting, or a NoInstallGate where there is none.</summary>
        public abstract IInstallGate CreateInstallGate(IDeviceBackend backend);

        /// <param name="restricted">Only administrators, or root, may use the channel. Off in tests.</param>
        public abstract IControlServer CreateControlServer(string name,
            Func<Dictionary<string, object>, Dictionary<string, object>> handler, Action<string> onProblem,
            bool restricted);

        /// <summary>
        /// Starts reporting devices that come and go. Returns null when the system
        /// cannot do that, in which case the loop relies on its regular passes.
        /// </summary>
        public abstract IDisposable WatchDevices(Action<int, string> onChange);

        /// <summary>
        /// Makes sure the data folders exist and that nobody without the right to
        /// can change what is in them. Problems are reported, not thrown: the
        /// folders must exist afterwards either way.
        /// </summary>
        public abstract void PrepareFolders(Action<string> onError);

        /// <summary>The system's own log, or null where there is none to write to.</summary>
        public virtual ISystemLog CreateSystemLog()
        {
            return null;
        }

        /// <summary>
        /// True when a keyboard or mouse that is not a USB device is working: the
        /// keyboard of a laptop, one on a PS/2 port, one over Bluetooth. LOTUS does
        /// not manage those, but it needs to know they are there before it decides
        /// that the computer has no working keyboard or mouse left.
        /// </summary>
        public virtual bool OtherInputWorks()
        {
            return false;
        }

        /// <summary>Tells the store how to keep its key on this system.</summary>
        public virtual void ConfigureStore(SecureStore store)
        {
        }

        /// <summary>LOTUS is being removed: let the uninstaller delete the data folder.</summary>
        public virtual void HandBackFolders()
        {
        }

        /// <summary>True when setup asked for a damaged policy store to be thrown away. The request is gone afterwards.</summary>
        public virtual bool TakeResetRequest()
        {
            return false;
        }

        /// <summary>Called once the list of blocked devices is loaded, for a backend that needs to consult it.</summary>
        public virtual void LedgerLoaded(Ledger ledger)
        {
        }

        /// <summary>The platform of the system this copy of LOTUS was built for.</summary>
        public static GuardPlatform Default()
        {
#if NETFRAMEWORK
            return new WindowsPlatform();
#else
            throw new InvalidOperationException("The service loop was started without a platform.");
#endif
        }
    }
}
