using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking.Proxy;

public class RedirectProxyService : IDisposable
{
    private readonly IAppLogger _logger;
    private readonly int _listenPort;
    private readonly int _singboxSocksPort;
    private TcpListener? _tcpListenerV4;
    private TcpListener? _tcpListenerV6;
    private UdpClient? _udpListenerV4;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, ProxyFlow> _activeFlows = new();
    private long _discordFlowCount;
    private long _nonDiscordFlowCount;
    private bool _isRunning;
    private bool _disposed;

    public long DiscordFlowCount => Interlocked.Read(ref _discordFlowCount);
    public long NonDiscordFlowCount => Interlocked.Read(ref _nonDiscordFlowCount);
    public int ActiveFlowCount => _activeFlows.Count;

    public RedirectProxyService(IAppLogger logger, int listenPort = 50180, int singboxSocksPort = 50181)
    {
        _logger = logger;
        _listenPort = listenPort;
        _singboxSocksPort = singboxSocksPort;
    }

    public Task<bool> StartAsync()
    {
        if (_isRunning) return Task.FromResult(true);

        try
        {
            _cts = new CancellationTokenSource();

            // 1. Start V4 Loopback Listener
            _tcpListenerV4 = new TcpListener(IPAddress.Loopback, _listenPort);
            _tcpListenerV4.Start();

            // 2. Start V6 Loopback Listener (if IPv6 supported)
            try
            {
                _tcpListenerV6 = new TcpListener(IPAddress.IPv6Loopback, _listenPort);
                _tcpListenerV6.Start();
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not bind IPv6 loopback listener: {ex.Message}");
            }

            // 3. Start UDP Listener
            try
            {
                _udpListenerV4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, _listenPort));
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not bind UDP loopback listener: {ex.Message}");
            }

            _isRunning = true;
            _logger.Info($"DCSS Redirect Proxy active on 127.0.0.1:{_listenPort} (Forwarding to sing-box socks5 127.0.0.1:{_singboxSocksPort})");

            _ = AcceptTcpLoopAsync(_tcpListenerV4, _cts.Token);
            if (_tcpListenerV6 != null)
            {
                _ = AcceptTcpLoopAsync(_tcpListenerV6, _cts.Token);
            }
            if (_udpListenerV4 != null)
            {
                _ = AcceptUdpLoopAsync(_udpListenerV4, _cts.Token);
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to start DCSS Redirect Proxy", ex);
            Stop();
            return Task.FromResult(false);
        }
    }

    private async Task AcceptTcpLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = HandleClientConnectionAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.Warning($"Error accepting TCP client: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleClientConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var flowId = Guid.NewGuid().ToString("N")[..8];
        try
        {
            // Query WFP connection redirect context or records
            var (originalDst, isDiscord) = QueryOriginalDestination(client.Client);
            if (isDiscord)
            {
                Interlocked.Increment(ref _discordFlowCount);
            }
            else
            {
                Interlocked.Increment(ref _nonDiscordFlowCount);
            }

            _logger.Info($"[Proxy Flow {flowId}] Received connection. Target: {originalDst} (IsDiscord: {isDiscord})");

            var flow = new ProxyFlow
            {
                FlowId = flowId,
                StartTime = DateTime.UtcNow,
                Target = originalDst,
                IsDiscord = isDiscord
            };
            _activeFlows[flowId] = flow;

            // Connect to sing-box local SOCKS5 inbound
            using var outbound = new TcpClient();
            await outbound.ConnectAsync(IPAddress.Loopback, _singboxSocksPort, ct).ConfigureAwait(false);

            using var clientStream = client.GetStream();
            using var outboundStream = outbound.GetStream();

            // Perform SOCKS5 Handshake with sing-box
            if (await PerformSocks5HandshakeAsync(outboundStream, originalDst, ct).ConfigureAwait(false))
            {
                // Bi-directional stream copying
                var t1 = clientStream.CopyToAsync(outboundStream, ct);
                var t2 = outboundStream.CopyToAsync(clientStream, ct);
                await Task.WhenAny(t1, t2).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.Debug($"[Proxy Flow {flowId}] Closed: {ex.Message}");
        }
        finally
        {
            _activeFlows.TryRemove(flowId, out _);
            client.Dispose();
        }
    }

    private (IPEndPoint Target, bool IsDiscord) QueryOriginalDestination(Socket socket)
    {
        try
        {
            // Query SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT
            byte[] contextBuffer = new byte[32]; // DCSS_REDIRECT_CONTEXT size
            int bytesReturned = socket.IOControl(
                Wfp.WfpNative.SIO_QUERY_WFP_CONNECTION_REDIRECT_CONTEXT,
                null,
                contextBuffer);

            if (bytesReturned >= 28)
            {
                byte af = contextBuffer[23];
                ushort port = BinaryPrimitives.ReadUInt16LittleEndian(contextBuffer.AsSpan(20, 2));
                if (port == 0)
                {
                    port = BinaryPrimitives.ReadUInt16BigEndian(contextBuffer.AsSpan(20, 2));
                }

                uint pid = BinaryPrimitives.ReadUInt32LittleEndian(contextBuffer.AsSpan(24, 4));

                IPAddress ip;
                if (af == 23) // AF_INET6
                {
                    ip = new IPAddress(contextBuffer.AsSpan(4, 16).ToArray());
                }
                else
                {
                    ip = new IPAddress(contextBuffer.AsSpan(0, 4).ToArray());
                }

                _logger.Debug($"[WFP Context] Extracted: {ip}:{port} (PID: {pid}, AF: {af})");
                return (new IPEndPoint(ip, port > 0 ? port : 443), true);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"[WFP Context Query] Not available or direct socket: {ex.Message}");
        }

        return (new IPEndPoint(IPAddress.Loopback, 80), true);
    }

    private async Task<bool> PerformSocks5HandshakeAsync(NetworkStream stream, IPEndPoint target, CancellationToken ct)
    {
        // 1. SOCKS5 greeting: [0x05 (VER), 0x01 (NMETHODS), 0x00 (NO AUTH)]
        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, ct).ConfigureAwait(false);

        byte[] resp = new byte[2];
        int read = await stream.ReadAsync(resp.AsMemory(0, 2), ct).ConfigureAwait(false);
        if (read < 2 || resp[0] != 0x05 || resp[1] != 0x00)
            return false;

        // 2. SOCKS5 Connect Request
        byte[] ipBytes = target.Address.GetAddressBytes();
        byte atyp = (byte)(target.AddressFamily == AddressFamily.InterNetwork ? 0x01 : 0x04);
        byte[] req = new byte[4 + ipBytes.Length + 2];
        req[0] = 0x05; // VER
        req[1] = 0x01; // CMD: CONNECT
        req[2] = 0x00; // RSV
        req[3] = atyp; // ATYP
        Array.Copy(ipBytes, 0, req, 4, ipBytes.Length);
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(4 + ipBytes.Length, 2), (ushort)target.Port);

        await stream.WriteAsync(req, ct).ConfigureAwait(false);

        byte[] connResp = new byte[req.Length];
        read = await stream.ReadAsync(connResp, ct).ConfigureAwait(false);
        return read >= 4 && connResp[1] == 0x00; // 0x00 = SUCCESS
    }

    private async Task AcceptUdpLoopAsync(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _discordFlowCount);
                // Handle Discord UDP datagram forwarding
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    _logger.Warning($"UDP receive error: {ex.Message}");
                }
            }
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
        _tcpListenerV4?.Stop();
        _tcpListenerV6?.Stop();
        _udpListenerV4?.Dispose();
        _activeFlows.Clear();
        _logger.Info("DCSS Redirect Proxy stopped.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    private class ProxyFlow
    {
        public string FlowId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public IPEndPoint? Target { get; set; }
        public bool IsDiscord { get; set; }
    }
}
