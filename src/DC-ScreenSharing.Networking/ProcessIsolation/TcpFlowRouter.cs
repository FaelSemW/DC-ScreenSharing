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
    private int _vpnInterfaceIndex;
    private IPAddress? _vpnInterfaceIp;
    private bool _isRunning;
    private long _totalBytesSent;
    private long _totalBytesReceived;

    public int ActiveSessionsCount => _activeSessions.Count;
    public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

    public TcpFlowRouter(FlowMappingTable flowTable)
    {
        _flowTable = flowTable;
    }

    public void SetInterfaceBinding(int interfaceIndex, IPAddress? interfaceIp)
    {
        _vpnInterfaceIndex = interfaceIndex;
        _vpnInterfaceIp = interfaceIp;
    }

    public void Start()
    {
        _isRunning = true;
    }

    public async Task StopAsync()
    {
        _isRunning = false;
        foreach (var session in _activeSessions.Values)
        {
            session.Dispose();
        }
        _activeSessions.Clear();
        await Task.CompletedTask;
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

        try
        {
            // Create VPN-bound socket
            var vpnSocket = CreateBoundVpnSocket(targetIp.AddressFamily);
            session.VpnSocket = vpnSocket;

            // Connect to remote target through VPN adapter
            var remoteEp = new IPEndPoint(targetIp, targetPort);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cts.Token);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(15)); // Connect timeout

            await vpnSocket.ConnectAsync(remoteEp, linkedCts.Token);

            // Start bidirectional streaming
            var clientToVpnTask = PumpDataAsync(clientSocket, vpnSocket, session, isOutbound: true, session.Cts.Token);
            var vpnToClientTask = PumpDataAsync(vpnSocket, clientSocket, session, isOutbound: false, session.Cts.Token);

            await Task.WhenAny(clientToVpnTask, vpnToClientTask);
        }
        catch { }
        finally
        {
            _activeSessions.TryRemove(flowKey, out _);
            session.Dispose();
        }
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
