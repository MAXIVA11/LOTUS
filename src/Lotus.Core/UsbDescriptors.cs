using System;
using System.Collections.Generic;
using Lotus.Core.Native;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>
    /// Reads a device's descriptors straight from the hub it is plugged into. This
    /// works the moment the device appears on the bus, before Windows has loaded a
    /// driver for it, and it still works while the device is disabled. LOTUS uses
    /// it to decide about a device without waiting for Windows to list its functions.
    /// </summary>
    public static class UsbDescriptors
    {
        private static readonly Guid HubInterface = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8");

        private const uint IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION = 0x00220410;
        private const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x00220448;
        private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x00220444;
        private const int ConnectionInfoSize = 35;
        private const int RequestHeaderSize = 12;
        private const int MaxConfigurations = 8;
        private const byte ConfigurationDescriptorType = 2;
        private const byte InterfaceDescriptorType = 4;

        /// <summary>Reads the descriptors of a connected device by its instance ID. Returns null on failure.</summary>
        public static UsbDescriptorInfo ReadById(string instanceId)
        {
            if (CfgMgr.CM_Locate_DevNodeW(out int node, instanceId, CfgMgr.CM_LOCATE_DEVNODE_NORMAL) !=
                CfgMgr.CR_SUCCESS)
            {
                return null;
            }
            return Read(node);
        }

        /// <summary>
        /// Reads the descriptors of the device behind a device node. Returns null
        /// when the hub does not answer, which the caller treats as "not known yet".
        /// </summary>
        internal static UsbDescriptorInfo Read(int node)
        {
            try
            {
                if (CfgMgr.CM_Get_Parent(out int parent, node, 0) != CfgMgr.CR_SUCCESS) return null;
                string parentId = CfgMgr.GetDeviceId(parent);
                if (string.IsNullOrEmpty(parentId)) return null;
                // For a USB device the "address" Windows keeps is the hub port it sits in.
                if (!CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_ADDRESS, out int port) || port <= 0 || port > 255) return null;

                foreach (string path in CfgMgr.GetInterfacePaths(HubInterface, parentId))
                {
                    UsbDescriptorInfo info = ReadFromHub(path, port);
                    if (info != null) return info;
                }
                return null;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException ||
                                       ex is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Makes the hub drop the device and detect it again, as if it had been
        /// unplugged and plugged back in. Needs administrator rights.
        /// </summary>
        internal static bool CyclePort(int node)
        {
            try
            {
                if (CfgMgr.CM_Get_Parent(out int parent, node, 0) != CfgMgr.CR_SUCCESS) return false;
                string parentId = CfgMgr.GetDeviceId(parent);
                if (string.IsNullOrEmpty(parentId)) return false;
                if (!CfgMgr.TryGetInt(node, CfgMgr.CM_DRP_ADDRESS, out int port) || port <= 0 || port > 255) return false;

                foreach (string path in CfgMgr.GetInterfacePaths(HubInterface, parentId))
                {
                    using (SafeFileHandle hub = Kernel.CreateFileW(path, Kernel.GENERIC_WRITE,
                               Kernel.FILE_SHARE_READ | Kernel.FILE_SHARE_WRITE, IntPtr.Zero, Kernel.OPEN_EXISTING,
                               0, IntPtr.Zero))
                    {
                        if (hub.IsInvalid) continue;
                        // USB_CYCLE_PORT_PARAMS: the port number, then a status the hub fills in.
                        var request = new byte[8];
                        BitConverter.GetBytes(port).CopyTo(request, 0);
                        if (Kernel.DeviceIoControl(hub, IOCTL_USB_HUB_CYCLE_PORT, request, request.Length, request,
                                request.Length, out int _, IntPtr.Zero))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException || ex is DllNotFoundException ||
                                       ex is ArgumentException)
            {
                return false;
            }
        }

        private static UsbDescriptorInfo ReadFromHub(string hubPath, int port)
        {
            using (SafeFileHandle hub = Kernel.CreateFileW(hubPath, Kernel.GENERIC_WRITE,
                       Kernel.FILE_SHARE_READ | Kernel.FILE_SHARE_WRITE, IntPtr.Zero, Kernel.OPEN_EXISTING, 0,
                       IntPtr.Zero))
            {
                if (hub.IsInvalid) return null;

                var connection = new byte[ConnectionInfoSize];
                BitConverter.GetBytes(port).CopyTo(connection, 0);
                if (!Kernel.DeviceIoControl(hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, connection,
                        connection.Length, connection, connection.Length, out int returned, IntPtr.Zero) ||
                    returned < 22)
                {
                    return null;
                }
                // Connection status sits at offset 31. Zero means nothing is plugged into the port.
                if (returned >= ConnectionInfoSize && BitConverter.ToInt32(connection, 31) == 0) return null;

                var info = new UsbDescriptorInfo
                {
                    DeviceClass = connection[8],
                    DeviceSubClass = connection[9],
                    DeviceProtocol = connection[10],
                    Configurations = connection[21]
                };

                int configurations = Math.Max(1, Math.Min((int)connection[21], MaxConfigurations));
                bool any = false;
                for (int index = 0; index < configurations; index++)
                {
                    byte[] descriptor = ReadConfiguration(hub, port, index);
                    if (descriptor == null) continue;
                    any = true;
                    ParseConfiguration(descriptor, info.Interfaces);
                }
                return any ? info : null;
            }
        }

        private static byte[] ReadConfiguration(SafeFileHandle hub, int port, int index)
        {
            int size = 1024;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var request = new byte[RequestHeaderSize + size];
                BitConverter.GetBytes(port).CopyTo(request, 0);
                // Setup packet: request type and request are filled in by the hub driver.
                BitConverter.GetBytes((ushort)((ConfigurationDescriptorType << 8) | index)).CopyTo(request, 6);
                BitConverter.GetBytes((ushort)0).CopyTo(request, 8);
                BitConverter.GetBytes((ushort)size).CopyTo(request, 10);
                if (!Kernel.DeviceIoControl(hub, IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION, request,
                        request.Length, request, request.Length, out int returned, IntPtr.Zero))
                {
                    return null;
                }
                int length = returned - RequestHeaderSize;
                if (length < 9 || request[RequestHeaderSize + 1] != ConfigurationDescriptorType) return null;

                int total = BitConverter.ToUInt16(request, RequestHeaderSize + 2);
                if (total > length && total > size && attempt == 0)
                {
                    // The descriptor is longer than the first guess. Ask again for all of it.
                    size = total;
                    continue;
                }
                var descriptor = new byte[Math.Min(length, Math.Max(total, 9))];
                Array.Copy(request, RequestHeaderSize, descriptor, 0, descriptor.Length);
                return descriptor;
            }
            return null;
        }

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
