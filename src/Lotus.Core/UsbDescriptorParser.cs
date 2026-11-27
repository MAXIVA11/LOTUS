using System.Collections.Generic;

namespace Lotus.Core
{
    /// <summary>One interface a USB device declares in its configuration descriptor.</summary>
    public struct UsbInterfaceInfo
    {
        public byte Class;
        public byte SubClass;
        public byte Protocol;

        /// <summary>The same text the USB bus driver publishes as a compatible ID.</summary>
        public string ToCompatibleId()
        {
            return @"USB\CLASS_" + Class.ToString("X2") + "&SUBCLASS_" + SubClass.ToString("X2") + "&PROT_" +
                   Protocol.ToString("X2");
        }
    }

    /// <summary>What a device says about itself on the wire, before any driver is involved.</summary>
    public sealed class UsbDescriptorInfo
    {
        public byte DeviceClass;
        public byte DeviceSubClass;
        public byte DeviceProtocol;
        public int Configurations;
        public List<UsbInterfaceInfo> Interfaces = new List<UsbInterfaceInfo>();

        /// <summary>Class codes as compatible IDs, the form the classifier already understands.</summary>
        public List<string> ToCompatibleIds()
        {
            var ids = new List<string>();
            foreach (UsbInterfaceInfo info in Interfaces)
            {
                string id = info.ToCompatibleId();
                if (!ids.Contains(id)) ids.Add(id);
            }
            // A device that is not described per interface carries its class in the device descriptor.
            if (DeviceClass != 0x00 && DeviceClass != 0xEF)
            {
                string id = @"USB\CLASS_" + DeviceClass.ToString("X2") + "&SUBCLASS_" +
                            DeviceSubClass.ToString("X2") + "&PROT_" + DeviceProtocol.ToString("X2");
                if (!ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }
    }

    public static partial class UsbDescriptors
    {
        private const byte InterfaceDescriptorType = 4;

        /// <summary>
        /// Collects the interface descriptors from one configuration. Every alternate
        /// setting counts, because a device may switch to it later.
        /// </summary>
        public static void ParseConfiguration(byte[] descriptor, List<UsbInterfaceInfo> into)
        {
            if (descriptor == null || into == null) return;
            int offset = 0;
            while (offset + 2 <= descriptor.Length)
            {
                int length = descriptor[offset];
                int type = descriptor[offset + 1];
                // A zero length would loop for ever. A truncated descriptor ends the walk.
                if (length < 2 || offset + length > descriptor.Length) break;
                if (type == InterfaceDescriptorType && length >= 9)
                {
                    var info = new UsbInterfaceInfo
                    {
                        Class = descriptor[offset + 5],
                        SubClass = descriptor[offset + 6],
                        Protocol = descriptor[offset + 7]
                    };
                    bool known = false;
                    foreach (UsbInterfaceInfo existing in into)
                    {
                        if (existing.Class == info.Class && existing.SubClass == info.SubClass &&
                            existing.Protocol == info.Protocol)
                        {
                            known = true;
                            break;
                        }
                    }
                    if (!known && into.Count < 64) into.Add(info);
                }
                offset += length;
            }
        }
    }
}
