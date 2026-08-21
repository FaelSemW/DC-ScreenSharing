using System.Net;
using System.Runtime.InteropServices;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public static class WinDivertNative
{
    private const string DllName = "WinDivert.dll";

    public const uint WINDIVERT_FLAG_SNIFF = 0x0001;
    public const uint WINDIVERT_FLAG_DROP = 0x0002;
    public const uint WINDIVERT_FLAG_RECV_ONLY = 0x0004;
    public const uint WINDIVERT_FLAG_READ_ONLY = 0x0004;
    public const uint WINDIVERT_FLAG_SEND_ONLY = 0x0008;
    public const uint WINDIVERT_FLAG_WRITE_ONLY = 0x0008;
    public const uint WINDIVERT_FLAG_NO_CHECKSUM = 0x0010;
    public const uint WINDIVERT_FLAG_FRAGMENTS = 0x0020;

    public const int WINDIVERT_PRIORITY_HIGHEST = 1000;
    public const int WINDIVERT_PRIORITY_HIGH = 500;
    public const int WINDIVERT_PRIORITY_NORMAL = 0;
    public const int WINDIVERT_PRIORITY_LOW = -500;
    public const int WINDIVERT_PRIORITY_LOWEST = -1000;

    public enum WINDIVERT_LAYER : uint
    {
        WINDIVERT_LAYER_NETWORK = 0,
        WINDIVERT_LAYER_NETWORK_FORWARD = 1,
        WINDIVERT_LAYER_FLOW = 2,
        WINDIVERT_LAYER_SOCKET = 3,
        WINDIVERT_LAYER_REFLECT = 4
    }

    public enum WINDIVERT_EVENT : byte
    {
        WINDIVERT_EVENT_NETWORK_PACKET = 0,
        WINDIVERT_EVENT_FLOW_ESTABLISHED = 1,
        WINDIVERT_EVENT_FLOW_DELETED = 2,
        WINDIVERT_EVENT_SOCKET_BIND = 3,
        WINDIVERT_EVENT_SOCKET_CONNECT = 4,
        WINDIVERT_EVENT_SOCKET_LISTEN = 5,
        WINDIVERT_EVENT_SOCKET_ACCEPT = 6,
        WINDIVERT_EVENT_SOCKET_CLOSE = 7,
        WINDIVERT_EVENT_REFLECT_OPEN = 8,
        WINDIVERT_EVENT_REFLECT_CLOSE = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDIVERT_DATA_NETWORK
    {
        public uint IfIdx;
        public uint SubIfIdx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDIVERT_DATA_SOCKET
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        public uint LocalAddr0;
        public uint LocalAddr1;
        public uint LocalAddr2;
        public uint LocalAddr3;
        public uint RemoteAddr0;
        public uint RemoteAddr1;
        public uint RemoteAddr2;
        public uint RemoteAddr3;
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct WINDIVERT_ADDRESS_UNION
    {
        [FieldOffset(0)]
        public WINDIVERT_DATA_NETWORK Network;

        [FieldOffset(0)]
        public WINDIVERT_DATA_SOCKET Socket;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDIVERT_ADDRESS
    {
        public long Timestamp;
        public byte Layer;
        public byte Event;
        public byte Sniffed;
        public byte Outbound;
        public byte Loopback;
        public byte Impostor;
        public byte IPv6;
        public byte IPChecksum;
        public byte TCPChecksum;
        public byte UDPChecksum;
        public byte Reserved1;
        public byte Reserved2;

        public WINDIVERT_ADDRESS_UNION Data;
    }

    [DllImport(DllName, SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr WinDivertOpen(
        [MarshalAs(UnmanagedType.LPStr)] string filter,
        WINDIVERT_LAYER layer,
        short priority,
        ulong flags);

    [DllImport(DllName, SetLastError = true)]
    public static extern bool WinDivertRecv(
        IntPtr handle,
        byte[] pPacket,
        uint packetLen,
        out uint pRecvLen,
        ref WINDIVERT_ADDRESS pAddr);

    [DllImport(DllName, SetLastError = true)]
    public static extern bool WinDivertSend(
        IntPtr handle,
        byte[] pPacket,
        uint packetLen,
        out uint pSendLen,
        ref WINDIVERT_ADDRESS pAddr);

    [DllImport(DllName, SetLastError = true)]
    public static extern bool WinDivertClose(IntPtr handle);

    [DllImport(DllName, SetLastError = true)]
    public static extern bool WinDivertHelperCalcChecksums(
        byte[] pPacket,
        uint packetLen,
        ref WINDIVERT_ADDRESS pAddr,
        ulong flags);

    // ======================================================================
    // PACKET HEADER PARSING HELPERS (IPv4, TCP, UDP)
    // ======================================================================

    public static bool ParseIPv4Header(
        byte[] packet,
        int length,
        out byte protocol,
        out IPAddress srcIp,
        out IPAddress dstIp,
        out int ipHeaderLen,
        out int totalLen)
    {
        protocol = 0;
        srcIp = IPAddress.None;
        dstIp = IPAddress.None;
        ipHeaderLen = 0;
        totalLen = 0;

        if (length < 20) return false;

        byte versionAndIhl = packet[0];
        byte version = (byte)(versionAndIhl >> 4);
        if (version != 4) return false;

        byte ihl = (byte)(versionAndIhl & 0x0F);
        ipHeaderLen = ihl * 4;
        if (ipHeaderLen < 20 || length < ipHeaderLen) return false;

        totalLen = (packet[2] << 8) | packet[3];
        protocol = packet[9];

        byte[] srcBytes = new byte[4];
        Array.Copy(packet, 12, srcBytes, 0, 4);
        srcIp = new IPAddress(srcBytes);

        byte[] dstBytes = new byte[4];
        Array.Copy(packet, 16, dstBytes, 0, 4);
        dstIp = new IPAddress(dstBytes);

        return true;
    }

    public static bool ParseTcpHeader(
        byte[] packet,
        int ipHeaderLen,
        int length,
        out ushort srcPort,
        out ushort dstPort,
        out uint seqNum,
        out uint ackNum,
        out byte flags,
        out int tcpHeaderLen)
    {
        srcPort = 0;
        dstPort = 0;
        seqNum = 0;
        ackNum = 0;
        flags = 0;
        tcpHeaderLen = 0;

        if (length < ipHeaderLen + 20) return false;

        srcPort = (ushort)((packet[ipHeaderLen] << 8) | packet[ipHeaderLen + 1]);
        dstPort = (ushort)((packet[ipHeaderLen + 2] << 8) | packet[ipHeaderLen + 3]);

        seqNum = (uint)((packet[ipHeaderLen + 4] << 24) |
                        (packet[ipHeaderLen + 5] << 16) |
                        (packet[ipHeaderLen + 6] << 8) |
                        packet[ipHeaderLen + 7]);

        ackNum = (uint)((packet[ipHeaderLen + 8] << 24) |
                        (packet[ipHeaderLen + 9] << 16) |
                        (packet[ipHeaderLen + 10] << 8) |
                        packet[ipHeaderLen + 11]);

        byte dataOffset = (byte)(packet[ipHeaderLen + 12] >> 4);
        tcpHeaderLen = dataOffset * 4;
        flags = packet[ipHeaderLen + 13];

        return tcpHeaderLen >= 20 && length >= ipHeaderLen + tcpHeaderLen;
    }

    public static bool ParseUdpHeader(
        byte[] packet,
        int ipHeaderLen,
        int length,
        out ushort srcPort,
        out ushort dstPort,
        out int udpLen)
    {
        srcPort = 0;
        dstPort = 0;
        udpLen = 0;

        if (length < ipHeaderLen + 8) return false;

        srcPort = (ushort)((packet[ipHeaderLen] << 8) | packet[ipHeaderLen + 1]);
        dstPort = (ushort)((packet[ipHeaderLen + 2] << 8) | packet[ipHeaderLen + 3]);
        udpLen = (packet[ipHeaderLen + 4] << 8) | packet[ipHeaderLen + 5];

        return udpLen >= 8 && length >= ipHeaderLen + 8;
    }

    public static void ModifyIPv4TcpDestination(
        byte[] packet,
        int length,
        int ipHeaderLen,
        IPAddress newDstIp,
        ushort newDstPort,
        ref WINDIVERT_ADDRESS addr)
    {
        byte[] newIpBytes = newDstIp.GetAddressBytes();
        Array.Copy(newIpBytes, 0, packet, 16, 4);

        packet[ipHeaderLen + 2] = (byte)(newDstPort >> 8);
        packet[ipHeaderLen + 3] = (byte)(newDstPort & 0xFF);

        addr.IPChecksum = 1;
        addr.TCPChecksum = 1;
        try
        {
            WinDivertHelperCalcChecksums(packet, (uint)length, ref addr, 0);
        }
        catch (DllNotFoundException) { }
    }
}
