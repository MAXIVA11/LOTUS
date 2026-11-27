namespace Lotus.Core
{
    /// <summary>
    /// The sentences in the activity log that name the operating system. The
    /// rules are the same everywhere. How the system is told to follow them is not,
    /// and the log should say what really happened on this machine.
    /// </summary>
    public static class SystemWords
    {
#if NETFRAMEWORK
        public const string GateOpened = "Windows installs new USB devices again.";

        public const string GateUnsupported =
            "This version of Windows cannot refuse new devices before they are installed. " +
            "LOTUS switches them off straight after they start instead.";

        public const string GateForeign =
            "This PC already has device installation restrictions that LOTUS did not set, so LOTUS " +
            "leaves them alone. It switches blocked devices off straight after they start instead.";

        public const string GateClosed =
            "Windows now refuses to install USB devices this policy blocks, so a device it has " +
            "never seen does not start at all.";

        public const string GateFailed = "Could not change the device installation restrictions. ";

        public const string RefusedRetryFailed = "Could not have Windows install the devices it refused earlier. ";

        public const string SwitchOnByHand = "Enable them in Device Manager.";

        public const string SystemDisk = "Windows runs from this device. LOTUS never blocks it.";

        public static string NotLetIn(string name)
        {
            return "Windows did not install " + name + " although the policy allows it. " +
                   "It may have no driver on this PC. Unplug it and plug it in again to retry.";
        }
#else
        public const string GateOpened = "The kernel switches new USB devices on by itself again.";

        public const string GateUnsupported =
            "This kernel cannot hold new USB devices back before they start. " +
            "LOTUS switches them off straight after they start instead.";

        public const string GateForeign =
            "Another program already holds the USB controllers shut, so LOTUS leaves them alone. " +
            "It switches blocked devices off straight after they start instead.";

        public const string GateClosed =
            "The kernel now holds every new USB device until LOTUS has looked at it, so a device " +
            "this policy blocks does not start at all.";

        public const string GateFailed = "Could not change the setting of the USB controllers. ";

        public const string RefusedRetryFailed = "Could not switch on the devices that were held back earlier. ";

        public const string SwitchOnByHand = "Unplug them and plug them in again.";

        public const string SystemDisk = "The system runs from this device. LOTUS never blocks it.";

        public static string NotLetIn(string name)
        {
            return "Could not switch on " + name + " although the policy allows it. " +
                   "Unplug it and plug it in again to retry.";
        }
#endif
    }
}
