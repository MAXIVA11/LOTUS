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
        /// <summary>
        /// A trust entry that names this device but no longer covers it, because the
        /// device now has a function it did not have when it was trusted.
        /// </summary>
        public TrustEntry Outgrown;

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

            bool storageBlocked = policy.Storage == StorageMode.Block;
            bool pnpBlocked = policy.Pnp == PnpMode.Block;

            TrustEntry trust = policy.FindTrust(device, out TrustEntry outgrown);
            if (trust != null)
            {
                // An entry that records what the device did can only be checked once the
                // device has said what it does now. Until then, wait instead of assuming.
                if (trust.Functions.Count > 0 && !device.FunctionsKnown && seenFor < IdentifyGrace &&
                    (storageBlocked || pnpBlocked))
                {
                    return Decision.Pending();
                }
                return Decision.Allow(Reason.Trusted, trust);
            }

            if (!storageBlocked && !pnpBlocked) return Decision.Allow(Reason.PolicyAllows);

            Decision decision = Judge(policy, device, seenFor, storageBlocked, pnpBlocked);
            decision.Outgrown = outgrown;
            return decision;
        }

        private static Decision Judge(Policy policy, UsbDevice device, TimeSpan seenFor, bool storageBlocked,
            bool pnpBlocked)
        {
            DeviceKinds kinds = device.Kinds;
            if (!device.FunctionsKnown)
            {
                // With both rules on and no exception for keyboards, every answer the
                // device could give ends in a block. There is nothing to wait for.
                if (storageBlocked && pnpBlocked && !policy.AllowInput && !device.BuiltIn)
                {
                    return Decision.Block(Reason.PnpBlocked);
                }
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

        /// <summary>The rule behind a block, worded to follow "because", for a full decision.</summary>
        public static string BlockReason(Decision decision)
        {
            string rule = BlockReason(decision.Reason);
            return decision.Outgrown == null
                ? rule
                : rule + ". It is on the trusted list, but it now has a function it did not have when it was trusted";
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
                    return SystemWords.SystemDisk;
                case Reason.Trusted:
                    if (decision.Trust != null && decision.Trust.Kind == TrustKind.Vendor)
                    {
                        return "Trusted. Every device from this vendor is allowed.";
                    }
                    return decision.Trust != null && decision.Trust.Kind == TrustKind.Model
                        ? "Trusted. Every device of this model is allowed."
                        : "Trusted. This exact device is allowed.";
                case Reason.BuiltIn:
                    return "Built into this computer, so the USB device rule leaves it alone.";
                case Reason.InputAllowed:
                    return "Allowed because the policy keeps keyboards and mice working.";
                case Reason.StorageBlocked:
                case Reason.PnpBlocked:
                    if (decision.Outgrown != null)
                    {
                        return "Blocked. It is on the trusted list, but it now has a function it did not have " +
                               "when it was trusted. Remove the trust and add it again if you expect that.";
                    }
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
