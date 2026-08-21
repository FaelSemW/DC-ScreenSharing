using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public class TcpBridgeSession : IDisposable
{
    public FlowKey Key { get; set; }
    public Socket? LocalSocket { get; set; }
    public Socket? VpnSocket { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    public long BytesSent;
    public long BytesReceived;

    public void Dispose()
    {
        Cts.Cancel();
        try { LocalSocket?.Close(); LocalSocket?.Dispose(); } catch { }
        try { VpnSocket?.Close(); VpnSocket?.Dispose(); } catch { }
        Cts.Dispose();
    }
}

public class TcpFlowRouter : IAsyncDisposable
{
    private readonly FlowMappingTable _flowTable;
    private readonly ConcurrentDictionary<FlowKey, TcpBridgeSession> _activeSessions = new();
    private readonly ConcurrentDictionary<ushort, (IPAddress TargetIp, ushort TargetPort)> _portToTargetMap = new();
    private TcpListener? _localListener;
    private CancellationTokenSource? _listenerCts;
    private int _vpnInterfaceIndex;
    private IPAddress? _vpnInterfaceIp;
    private bool _isRunning;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private long _totalConnectionsProxied;

    public int ListenPort { get; private set; } = 15889;
    public int ProxyPort { get; set; } = 15888;
    public int ActiveSessionsCount => _activeSessions.Count;
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
    public long TotalConnectionsProxied => Interlocked.Read(ref _totalConnectionsProxied);

    public TcpFlowRouter(FlowMappingTable flowTable)
    {
        _flowTable = flowTable;
    }

    public void SetInterfaceBinding(int interfaceIndex, IPAddress? interfaceIp)
    {
        _vpnInterfaceIndex = interfaceIndex;
        _vpnInterfaceIp = interfaceIp;
    }

    public void RegisterTargetMapping(ushort localPort, IPAddress targetIp, ushort targetPort)
    {
        _portToTargetMap[localPort] = (targetIp, targetPort);
    }

    public void Start(int listenPort = 15889)
    {
        if (_isRunning) return;

        ListenPort = listenPort;
        _isRunning = true;
        _listenerCts = new CancellationTokenSource();

        try
        {
            _localListener = new TcpListener(IPAddress.Loopback, ListenPort);
            _localListener.Start(100);
            _ = Task.Run(() => AcceptConnectionsLoopAsync(_listenerCts.Token));
        }
        catch (Exception)
        {
            // If preferred port is taken, bind to dynamic loopback port
            try
            {
                _localListener = new TcpListener(IPAddress.Loopback, 0);
                _localListener.Start(100);
                ListenPort = ((IPEndPoint)_localListener.LocalEndpoint).Port;
                _ = Task.Run(() => AcceptConnectionsLoopAsync(_listenerCts.Token));
            }
            catch { }
        }
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        _listenerCts?.Cancel();

        try
        {
            _localListener?.Stop();
        }
        catch { }

        foreach (var session in _activeSessions.Values)
        {
            session.Dispose();
        }
        _activeSessions.Clear();
        _portToTargetMap.Clear();

        await Task.CompletedTask;
    }

    private async Task AcceptConnectionsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning && _localListener != null)
        {
            try
            {
                var clientSocket = await _localListener.AcceptSocketAsync(ct);
                _ = Task.Run(() => HandleAcceptedConnectionAsync(clientSocket, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                if (!_isRunning) break;
                await Task.Delay(50, ct);
            }
        }
    }

    private async Task HandleAcceptedConnectionAsync(Socket clientSocket, CancellationToken ct)
    {
        try
        {
            var remoteEp = clientSocket.RemoteEndPoint as IPEndPoint;
            if (remoteEp == null)
            {
                clientSocket.Dispose();
                return;
            }

            ushort clientPort = (ushort)remoteEp.Port;
            IPAddress? targetIp = null;
            ushort targetPort = 0;

            // Lookup original destination from port mapping table
            if (_portToTargetMap.TryGetValue(clientPort, out var mapping))
            {
                targetIp = mapping.TargetIp;
                targetPort = mapping.TargetPort;
            }
            else
            {
                // Fallback: search flow table
                foreach (var flow in _flowTable.GetAllFlows())
                {
                    if (flow.Key.LocalPort == clientPort && flow.IsTargetFlow)
                    {
                        targetIp = new IPAddress(flow.Key.RemoteIp);
                        targetPort = flow.Key.RemotePort;
                        break;
                    }
                }
            }

            if (targetIp == null || targetPort == 0)
            {
                clientSocket.Dispose();
                return;
            }

            var flowKey = FlowKey.FromEndpoints(6, remoteEp.Address, clientPort, targetIp, targetPort);
            await BridgeConnectionAsync(clientSocket, targetIp, targetPort, flowKey, ct);
        }
        catch
        {
            try { clientSocket.Dispose(); } catch { }
        }
    }

    public async Task BridgeConnectionAsync(
        Socket clientSocket,
        IPAddress targetIp,
        ushort targetPort,
        FlowKey flowKey,
        CancellationToken ct = default)
    {
        if (!_isRunning)
        {
            clientSocket.Dispose();
            return;
        }

        var session = new TcpBridgeSession
        {
            Key = flowKey,
            LocalSocket = clientSocket
        };

        _activeSessions[flowKey] = session;
        Interlocked.Increment(ref _totalConnectionsProxied);

        try
        {
            Socket outboundSocket;

            // Priority 1: Use local SOCKS5 proxy transport (sing-box WireGuard / OpenVPN loopback inbound)
            if (ProxyPort > 0)
            {
                outboundSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                await outboundSocket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, ProxyPort), connectCts.Token);

                // SOCKS5 handshake
                await PerformSocks5ConnectAsync(outboundSocket, targetIp, targetPort, connectCts.Token);
            }
            else
            {
                // Priority 2: Direct VPN interface binding
                outboundSocket = CreateBoundVpnSocket(targetIp.AddressFamily);
                var remoteEp = new IPEndPoint(targetIp, targetPort);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);
                linkedCts.CancelAfter(TimeSpan.FromSeconds(15));
                await outboundSocket.ConnectAsync(remoteEp, linkedCts.Token);
            }

            session.VpnSocket = outboundSocket;

            // Start bidirectional streaming
            var clientToVpnTask = PumpDataAsync(clientSocket, outboundSocket, session, isOutbound: true, session.Cts.Token);
            var vpnToClientTask = PumpDataAsync(outboundSocket, clientSocket, session, isOutbound: false, session.Cts.Token);

            await Task.WhenAny(clientToVpnTask, vpnToClientTask);
        }
        catch { }
        finally
        {
            _activeSessions.TryRemove(flowKey, out _);
            _portToTargetMap.TryRemove(flowKey.LocalPort, out _);
            session.Dispose();
        }
    }

    private static async Task PerformSocks5ConnectAsync(
        Socket sock,
        IPAddress targetIp,
        ushort targetPort,
        CancellationToken ct)
    {
        // 1. SOCKS5 Greeting: [Version=5, NMethods=1, Method=0 (No Auth)]
        await sock.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, SocketFlags.None, ct);
        byte[] authResponse = new byte[2];
        int authRead = await sock.ReceiveAsync(authResponse, SocketFlags.None, ct);
        if (authRead < 2 || authResponse[0] != 0x05 || authResponse[1] != 0x00)
        {
            throw new InvalidOperationException("SOCKS5 proxy authentication negotiation failed.");
        }

        // 2. SOCKS5 Connect Request: [Ver=5, Cmd=1 (CONNECT), Rsv=0, Atyp=1 (IPv4), DstIp(4), DstPort(2)]
        byte[] targetIpBytes = targetIp.GetAddressBytes();
        byte[] request = new byte[10];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = 0x01; // IPv4
        Buffer.BlockCopy(targetIpBytes, 0, request, 4, 4);
        request[8] = (byte)(targetPort >> 8);
        request[9] = (byte)(targetPort & 0xFF);

        await sock.SendAsync(request, SocketFlags.None, ct);

        byte[] reply = new byte[10];
        int replyRead = await sock.ReceiveAsync(reply, SocketFlags.None, ct);
        if (replyRead < 2 || reply[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 connect to {targetIp}:{targetPort} failed with reply code {reply[1]}.");
        }
    }

    public Socket CreateBoundVpnSocket(AddressFamily addressFamily = AddressFamily.InterNetwork)
    {
        var socket = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        if (_vpnInterfaceIndex > 0)
        {
            InterfaceBindingService.BindSocketToInterface(socket, _vpnInterfaceIndex, _vpnInterfaceIp);
        }

        return socket;
    }

    private async Task PumpDataAsync(
        Socket src,
        Socket dst,
        TcpBridgeSession session,
        bool isOutbound,
        CancellationToken ct)
    {
        byte[] buffer = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested && src.Connected && dst.Connected)
            {
                int bytesRead = await src.ReceiveAsync(buffer, SocketFlags.None, ct);
                if (bytesRead == 0) break; // EOF

                await dst.SendAsync(new ReadOnlyMemory<byte>(buffer, 0, bytesRead), SocketFlags.None, ct);

                session.LastActivityUtc = DateTime.UtcNow;
                if (isOutbound)
                {
                    Interlocked.Add(ref session.BytesSent, bytesRead);
                    Interlocked.Add(ref _totalBytesSent, bytesRead);
                }
                else
                {
                    Interlocked.Add(ref session.BytesReceived, bytesRead);
                    Interlocked.Add(ref _totalBytesReceived, bytesRead);
                }
            }
        }
        catch { }
        finally
        {
            try { dst.Shutdown(SocketShutdown.Send); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
