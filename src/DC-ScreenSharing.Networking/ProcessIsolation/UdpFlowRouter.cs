using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public class UdpNatSession : IDisposable
{
    public FlowKey Key { get; set; }
    public IPEndPoint LocalEndpoint { get; set; } = new(IPAddress.Any, 0);
    public IPEndPoint RemoteEndpoint { get; set; } = new(IPAddress.Any, 0);
    public Socket? VpnUdpSocket { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    public long BytesSent;
    public long BytesReceived;

    public void Dispose()
    {
        Cts.Cancel();
        try { VpnUdpSocket?.Close(); VpnUdpSocket?.Dispose(); } catch { }
        Cts.Dispose();
    }
}

public class UdpFlowRouter : IAsyncDisposable
{
    private readonly FlowMappingTable _flowTable;
    private readonly ConcurrentDictionary<FlowKey, UdpNatSession> _activeNatSessions = new();
    private int _vpnInterfaceIndex;
    private IPAddress? _vpnInterfaceIp;
    private IntPtr _winDivertHandle = IntPtr.Zero;
    private bool _isRunning;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private readonly Timer _cleanupTimer;

    public int ActiveSessionsCount => _activeNatSessions.Count;
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    public UdpFlowRouter(FlowMappingTable flowTable)
    {
        _flowTable = flowTable;
        _cleanupTimer = new Timer(OnCleanupTick, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    public void SetInterfaceBinding(int interfaceIndex, IPAddress? interfaceIp)
    {
        _vpnInterfaceIndex = interfaceIndex;
        _vpnInterfaceIp = interfaceIp;
    }

    public void SetWinDivertHandle(IntPtr handle)
    {
        _winDivertHandle = handle;
    }

    public void Start()
    {
        _isRunning = true;
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        foreach (var session in _activeNatSessions.Values)
        {
            session.Dispose();
        }
        _activeNatSessions.Clear();
        await Task.CompletedTask;
    }

    public async Task RouteOutboundUdpPacketAsync(
        byte[] payload,
        int payloadOffset,
        int payloadLength,
        IPAddress localIp,
        ushort localPort,
        IPAddress remoteIp,
        ushort remotePort,
        FlowKey flowKey,
        CancellationToken ct = default)
    {
        if (!_isRunning) return;

        var session = _activeNatSessions.GetOrAdd(flowKey, k =>
        {
            var newSession = new UdpNatSession
            {
                Key = k,
                LocalEndpoint = new IPEndPoint(localIp, localPort),
                RemoteEndpoint = new IPEndPoint(remoteIp, remotePort)
            };

            try
            {
                var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (_vpnInterfaceIndex > 0)
                {
                    InterfaceBindingService.BindSocketToInterface(sock, _vpnInterfaceIndex, _vpnInterfaceIp);
                }

                newSession.VpnUdpSocket = sock;

                // Start background receive loop for return traffic from VPN endpoint
                _ = Task.Run(() => ReceiveVpnUdpResponsesAsync(newSession, newSession.Cts.Token));
            }
            catch (Exception)
            {
                newSession.Dispose();
                throw;
            }

            return newSession;
        });

        if (session.VpnUdpSocket != null && session.VpnUdpSocket.IsBound)
        {
            try
            {
                var targetEp = new IPEndPoint(remoteIp, remotePort);
                var segment = new ReadOnlyMemory<byte>(payload, payloadOffset, payloadLength);
                int sent = await session.VpnUdpSocket.SendToAsync(segment, SocketFlags.None, targetEp, ct);

                session.LastActivityUtc = DateTime.UtcNow;
                Interlocked.Add(ref session.BytesSent, sent);
                Interlocked.Add(ref _totalBytesSent, sent);
            }
            catch { }
        }
    }

    private async Task ReceiveVpnUdpResponsesAsync(UdpNatSession session, CancellationToken ct)
    {
        byte[] buffer = new byte[65535];
        EndPoint remoteSender = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!ct.IsCancellationRequested && _isRunning && session.VpnUdpSocket != null)
            {
                var res = await session.VpnUdpSocket.ReceiveFromAsync(buffer, SocketFlags.None, remoteSender);
                int bytesRead = res.ReceivedBytes;
                if (bytesRead <= 0) break;

                session.LastActivityUtc = DateTime.UtcNow;
                Interlocked.Add(ref session.BytesReceived, bytesRead);
                Interlocked.Add(ref _totalBytesReceived, bytesRead);

                // If WinDivert handle is available, construct synthetic IPv4+UDP packet and re-inject
                if (_winDivertHandle != IntPtr.Zero && _winDivertHandle != new IntPtr(-1))
                {
                    InjectInboundUdpPacket(
                        buffer,
                        bytesRead,
                        srcIp: session.RemoteEndpoint.Address,
                        srcPort: (ushort)session.RemoteEndpoint.Port,
                        dstIp: session.LocalEndpoint.Address,
                        dstPort: (ushort)session.LocalEndpoint.Port);
                }
            }
        }
        catch { }
        finally
        {
            _activeNatSessions.TryRemove(session.Key, out _);
            session.Dispose();
        }
    }

    private void InjectInboundUdpPacket(
        byte[] payload,
        int payloadLen,
        IPAddress srcIp,
        ushort srcPort,
        IPAddress dstIp,
        ushort dstPort)
    {
        try
        {
            int totalLen = 20 + 8 + payloadLen;
            byte[] packet = new byte[totalLen];

            // 1. IPv4 Header
            packet[0] = 0x45; // Version 4, IHL 5
            packet[1] = 0x00; // DSCP / ECN
            packet[2] = (byte)(totalLen >> 8);
            packet[3] = (byte)(totalLen & 0xFF);
            packet[4] = 0x00; // Identification
            packet[5] = 0x00;
            packet[6] = 0x40; // Flags (Don't Fragment)
            packet[7] = 0x00;
            packet[8] = 64;   // TTL
            packet[9] = 17;   // Protocol 17 = UDP
            packet[10] = 0x00; // Header Checksum (calculated by WinDivert)
            packet[11] = 0x00;

            byte[] srcBytes = srcIp.GetAddressBytes();
            byte[] dstBytes = dstIp.GetAddressBytes();
            Array.Copy(srcBytes, 0, packet, 12, 4);
            Array.Copy(dstBytes, 0, packet, 16, 4);

            // 2. UDP Header
            packet[20] = (byte)(srcPort >> 8);
            packet[21] = (byte)(srcPort & 0xFF);
            packet[22] = (byte)(dstPort >> 8);
            packet[23] = (byte)(dstPort & 0xFF);
            int udpLen = 8 + payloadLen;
            packet[24] = (byte)(udpLen >> 8);
            packet[25] = (byte)(udpLen & 0xFF);
            packet[26] = 0x00; // Checksum
            packet[27] = 0x00;

            // 3. Payload
            Array.Copy(payload, 0, packet, 28, payloadLen);

            // 4. Calculate Checksums & Re-inject via WinDivert
            var addr = new WinDivertNative.WINDIVERT_ADDRESS
            {
                Outbound = 0, // Inbound packet towards target application
                IPChecksum = 1,
                UDPChecksum = 1
            };

            WinDivertNative.WinDivertHelperCalcChecksums(packet, (uint)totalLen, ref addr, 0);
            WinDivertNative.WinDivertSend(_winDivertHandle, packet, (uint)totalLen, out _, ref addr);
        }
        catch { }
    }

    private void OnCleanupTick(object? state)
    {
        if (!_isRunning) return;

        var now = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(30); // 30s UDP flow inactivity timeout

        foreach (var kvp in _activeNatSessions)
        {
            if (now - kvp.Value.LastActivityUtc > timeout)
            {
                if (_activeNatSessions.TryRemove(kvp.Key, out var expired))
                {
                    expired.Dispose();
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cleanupTimer.Dispose();
        await StopAsync();
    }
}
