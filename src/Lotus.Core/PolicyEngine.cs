using System;

namespace Lotus.Core
{
    public enum Verdict
    {
        Allow,
        Block,
        /// <summary>The device was plugged in a moment ago and has not said what it is yet.</summary>
        Pending
    }

    public enum Reason
    {
        /// <summary>LOTUS is disarmed.</summary>
        Disarmed,
        /// <summary>Windows runs from this device.</summary>
        SystemDisk,
        /// <summary>The device is on the trusted list.</summary>
        Trusted,
        /// <summary>No rule in the policy applies to this device.</summary>
        PolicyAllows,
        /// <summary>The device is part of the computer itself.</summary>
        BuiltIn,
        /// <summary>The device is a keyboard or mouse and the policy keeps those working.</summary>
        InputAllowed,
        /// <summary>Removable storage is blocked.</summary>
        StorageBlocked,
        /// <summary>Untrusted USB devices are blocked.</summary>
        PnpBlocked,
        /// <summary>Waiting for the device to list its functions.</summary>
        Identifying
    }

    public sealed class Decision
    {
        public Verdict Verdict;
        public Reason Reason;
        /// <summary>The trust entry that matched, when Reason is Trusted.</summary>
        public TrustEntry Trust;

        public bool IsBlock => Verdict == Verdict.Block;

        public static Decision Allow(Reason reason, TrustEntry trust = null)
        {
            return new Decision { Verdict = Verdict.Allow, Reason = reason, Trust = trust };
        }

        public static Decision Block(Reason reason)
        {
            return new Decision { Verdict = Verdict.Block, Reason = reason };
        }

        public static Decision Pending()
        {
            return new Decision { Verdict = Verdict.Pending, Reason = Reason.Identifying };
        }
    }

    /// <summary>
    /// Decides what happens to one USB device under a policy. It has no side effects
    /// and reads nothing from the system, so the service, the app and the tests all
    /// get the same answer for the same input.
    /// </summary>
    public static class PolicyEngine
    {
        /// <summary>
        /// How long a freshly plugged composite device may take to list its
        /// interfaces before LOTUS stops waiting and treats it as an unknown device.
        /// </summary>
        public static readonly TimeSpan IdentifyGrace = TimeSpan.FromSeconds(3);

        /// <param name="seenFor">How long the device has been connected.</param>
        public static Decision Evaluate(Policy policy, UsbDevice device, TimeSpan seenFor)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            if (device == null) throw new ArgumentNullException(nameof(device));

            if (!policy.Armed) return Decision.Allow(Reason.Disarmed);

            // Never touch the disk Windows is running from.
            if (device.IsSystemDisk) return Decision.Allow(Reason.SystemDisk);

            TrustEntry trust = policy.FindTrust(device);
            if (trust != null) return Decision.Allow(Reason.Trusted, trust);

            bool storageBlocked = policy.Storage == StorageMode.Block;
            bool pnpBlocked = policy.Pnp == PnpMode.Block;
            if (!storageBlocked && !pnpBlocked) return Decision.Allow(Reason.PolicyAllows);

            DeviceKinds kinds = device.Kinds;
            if (!device.FunctionsKnown)
            {
                if (seenFor < IdentifyGrace) return Decision.Pending();
                // It never told us what it is. Treat it as an unknown device.
                kinds = DeviceKinds.Other;
            }
            if (kinds == DeviceKinds.None) kinds = DeviceKinds.Other;

            bool hasStorage = (kinds & (DeviceKinds.Storage | DeviceKinds.Portable)) != 0;
            if (hasStorage && storageBlocked) return Decision.Block(Reason.StorageBlocked);

            if (pnpBlocked)
            {
                DeviceKinds other = kinds & ~(DeviceKinds.Storage | DeviceKinds.Portable);
                if (other != DeviceKinds.None)
                {
                    if (device.BuiltIn) return Decision.Allow(Reason.BuiltIn);
                    if (policy.AllowInput && other == DeviceKinds.Input) return Decision.Allow(Reason.InputAllowed);
                    return Decision.Block(Reason.PnpBlocked);
                }
            }

            return Decision.Allow(Reason.PolicyAllows);
        }

        /// <summary>The rule behind a block, worded to follow "because".</summary>
        public static string BlockReason(Reason reason)
        {
            return reason == Reason.StorageBlocked
                ? "removable storage is blocked and this device is not trusted"
                : "only trusted USB devices are allowed";
        }

        /// <summary>One sentence that explains a decision to the person looking at the device.</summary>
        public static string Explain(Decision decision, Policy policy)
        {
            switch (decision.Reason)
            {
                case Reason.Disarmed:
                    return "LOTUS is disarmed, so nothing is blocked.";
                case Reason.SystemDisk:
                    return "Windows runs from this device. LOTUS never blocks it.";
                case Reason.Trusted:
                    return decision.Trust != null && decision.Trust.Kind == TrustKind.Model
                        ? "Trusted. Every device of this model is allowed."
                        : "Trusted. This exact device is allowed.";
                case Reason.BuiltIn:
                    return "Built into this computer, so the USB device rule leaves it alone.";
                case Reason.InputAllowed:
                    return "Allowed because the policy keeps keyboards and mice working.";
                case Reason.StorageBlocked:
                case Reason.PnpBlocked:
                    return "Blocked because " + BlockReason(decision.Reason) + ".";
                case Reason.Identifying:
                    return "Just plugged in. Waiting for the device to say what it is.";
                default:
                    if (policy != null && policy.Storage == StorageMode.ReadOnly)
                    {
                        return "Allowed. Removable storage is read-only.";
                    }
                    return "Allowed by the policy.";
            }
        }
    }
}
