using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public class WinDivertProcessIsolationEngine : IProcessIsolationEngine
{
    private readonly FlowMappingTable _flowTable;
    private readonly ProcessIdentityResolver _identityResolver;
    private readonly TcpFlowRouter _tcpRouter;
    private readonly UdpFlowRouter _udpRouter;

    private IntPtr _divertHandle = IntPtr.Zero;
    private IntPtr _socketObserverHandle = IntPtr.Zero;
    private CancellationTokenSource? _engineCts;
    private Task? _packetWorkerTask;
    private Task? _socketObserverTask;

    private ProcessIsolationOptions? _options;
    private readonly Stopwatch _uptimeSw = new();
    private bool _isRunning;
    private readonly object _stateLock = new();

    // Diagnostic flow counters
    private long _targetTcpDetected;
    private long _targetTcpProxied;
    private long _targetTcpBypassed;
    private long _targetUdpDetected;
    private long _targetUdpProxied;
    private long _targetUdpBypassed;

    public bool IsRunning => _isRunning;
    public long TargetTcpDetected => Interlocked.Read(ref _targetTcpDetected);
    public long TargetTcpProxied => Interlocked.Read(ref _targetTcpProxied);
    public long TargetTcpBypassed => Interlocked.Read(ref _targetTcpBypassed);
    public long TargetUdpDetected => Interlocked.Read(ref _targetUdpDetected);
    public long TargetUdpProxied => Interlocked.Read(ref _targetUdpProxied);
    public long TargetUdpBypassed => Interlocked.Read(ref _targetUdpBypassed);

    public WinDivertProcessIsolationEngine()
    {
        _flowTable = new FlowMappingTable();
        _identityResolver = new ProcessIdentityResolver();
        _tcpRouter = new TcpFlowRouter(_flowTable);
        _udpRouter = new UdpFlowRouter(_flowTable);
    }

    public Task StartAsync(ProcessIsolationOptions options, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_isRunning) return Task.CompletedTask;

            _options = options;
            _identityResolver.UpdateTargetNames(options.TargetProcessNames);
            _tcpRouter.SetInterfaceBinding(options.VpnInterfaceIndex, options.VpnInterfaceIp);
            _udpRouter.SetInterfaceBinding(options.VpnInterfaceIndex, options.VpnInterfaceIp);

            _tcpRouter.Start();
            _udpRouter.Start();

            _engineCts = new CancellationTokenSource();

            // Open WinDivert handles
            try
            {
                // Network layer filter: capture outbound IPv4 TCP and UDP (avoid loopback)
                string netFilter = "outbound and ip and (tcp or udp) and not loopback";
                _divertHandle = WinDivertNative.WinDivertOpen(
                    netFilter,
                    WinDivertNative.WINDIVERT_LAYER.WINDIVERT_LAYER_NETWORK,
                    WinDivertNative.WINDIVERT_PRIORITY_NORMAL,
                    0);

                if (_divertHandle == IntPtr.Zero || _divertHandle == new IntPtr(-1))
                {
                    int err = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"[WinDivertEngine] WinDivertOpen returned error {err}. Running in user-space fallback mode.");
                }
                else
                {
                    _udpRouter.SetWinDivertHandle(_divertHandle);
                    _packetWorkerTask = Task.Run(() => PacketWorkerLoopAsync(_engineCts.Token));
                }

                // Socket layer observer for fast PID attribution
                try
                {
                    string socketFilter = "true";
                    _socketObserverHandle = WinDivertNative.WinDivertOpen(
                        socketFilter,
                        WinDivertNative.WINDIVERT_LAYER.WINDIVERT_LAYER_SOCKET,
                        WinDivertNative.WINDIVERT_PRIORITY_LOW,
                        WinDivertNative.WINDIVERT_FLAG_SNIFF | WinDivertNative.WINDIVERT_FLAG_READ_ONLY);

                    if (_socketObserverHandle != IntPtr.Zero && _socketObserverHandle != new IntPtr(-1))
                    {
                        _socketObserverTask = Task.Run(() => SocketObserverLoopAsync(_engineCts.Token));
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WinDivertEngine] Initialization exception: {ex.Message}");
            }

            _isRunning = true;
            _uptimeSw.Restart();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (!_isRunning) return;
            _isRunning = false;
            _uptimeSw.Stop();

            _engineCts?.Cancel();
        }

        // Close handles to unblock worker threads
        if (_divertHandle != IntPtr.Zero && _divertHandle != new IntPtr(-1))
        {
            try { WinDivertNative.WinDivertClose(_divertHandle); } catch { }
            _divertHandle = IntPtr.Zero;
        }

        if (_socketObserverHandle != IntPtr.Zero && _socketObserverHandle != new IntPtr(-1))
        {
            try { WinDivertNative.WinDivertClose(_socketObserverHandle); } catch { }
            _socketObserverHandle = IntPtr.Zero;
        }

        if (_packetWorkerTask != null)
        {
            try { await _packetWorkerTask.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
            _packetWorkerTask = null;
        }

        if (_socketObserverTask != null)
        {
            try { await _socketObserverTask.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch { }
            _socketObserverTask = null;
        }

        await _tcpRouter.StopAsync();
        await _udpRouter.StopAsync();
        _flowTable.Clear();
        _engineCts?.Dispose();
        _engineCts = null;
    }

    public void UpdateTargetProcesses(IEnumerable<string> targetProcessNames)
    {
        _identityResolver.UpdateTargetNames(targetProcessNames);
    }

    public void SetInterfaceBinding(int interfaceIndex, IPAddress interfaceIp)
    {
        _options ??= new ProcessIsolationOptions();
        _options.VpnInterfaceIndex = interfaceIndex;
        _options.VpnInterfaceIp = interfaceIp;

        _tcpRouter.SetInterfaceBinding(interfaceIndex, interfaceIp);
        _udpRouter.SetInterfaceBinding(interfaceIndex, interfaceIp);
    }

    public ProcessIsolationStats GetStats()
    {
        var (tcpCount, udpCount) = _flowTable.GetActiveCounts();
        return new ProcessIsolationStats
        {
            IsRunning = _isRunning,
            ActiveTcpFlows = tcpCount + _tcpRouter.ActiveSessionsCount,
            ActiveUdpFlows = udpCount + _udpRouter.ActiveSessionsCount,
            TotalTcpBytesSent = _tcpRouter.TotalBytesSent,
            TotalTcpBytesReceived = _tcpRouter.TotalBytesReceived,
            TotalUdpBytesSent = _udpRouter.TotalBytesSent,
            TotalUdpBytesReceived = _udpRouter.TotalBytesReceived,
            TrackedProcessesCount = _identityResolver.GetTrackedPidCount(),
            TransportName = _options?.TransportType ?? "OpenVPN",
            VpnInterfaceIndex = _options?.VpnInterfaceIndex ?? 0,
            VpnInterfaceIp = _options?.VpnInterfaceIp?.ToString(),
            UptimeSeconds = _uptimeSw.Elapsed.TotalSeconds
        };
    }

    // ======================================================================
    // PACKET WORKER LOOP (USER-MODE INTERCEPTION & FORWARDING)
    // ======================================================================

    private void PacketWorkerLoopAsync(CancellationToken ct)
    {
        byte[] packetBuffer = new byte[65535];
        var addr = new WinDivertNative.WINDIVERT_ADDRESS();

        while (!ct.IsCancellationRequested && _divertHandle != IntPtr.Zero && _divertHandle != new IntPtr(-1))
        {
            if (!WinDivertNative.WinDivertRecv(_divertHandle, packetBuffer, (uint)packetBuffer.Length, out uint recvLen, ref addr))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 995 || err == 6) break; // Operation aborted or invalid handle
                continue;
            }

            if (recvLen == 0) continue;

            // Process packet
            bool handled = ProcessOutboundPacket(packetBuffer, (int)recvLen, ref addr);

            // If not intercepted by routing engine, re-inject immediately (untouched normal non-target traffic)
            if (!handled)
            {
                WinDivertNative.WinDivertSend(_divertHandle, packetBuffer, recvLen, out _, ref addr);
            }
        }
    }

    private bool ProcessOutboundPacket(byte[] packet, int length, ref WinDivertNative.WINDIVERT_ADDRESS addr)
    {
        if (!WinDivertNative.ParseIPv4Header(packet, length, out byte proto, out var srcIp, out var dstIp, out int ipHdrLen, out int totalLen))
        {
            return false;
        }

        // Avoid capturing VPN tunnel server traffic itself (e.g. WireGuard / OpenVPN outer UDP/TCP packets)
        if (_options?.VpnServerIp != null && dstIp.Equals(_options.VpnServerIp))
        {
            return false; // Let outer VPN transport packet flow direct
        }

        if (proto == 17) // UDP
        {
            if (WinDivertNative.ParseUdpHeader(packet, ipHdrLen, length, out ushort srcPort, out ushort dstPort, out int udpLen))
            {
                var flowKey = FlowKey.FromEndpoints(17, srcIp, srcPort, dstIp, dstPort);

                // Check existing flow or resolve PID
                if (_flowTable.TryGetFlow(flowKey, out var flow) && flow != null)
                {
                    if (!flow.IsTargetFlow) return false;

                    flow.Touch(bytesSent: udpLen);
                    Interlocked.Increment(ref _targetUdpProxied);

                    int payloadOffset = ipHdrLen + 8;
                    int payloadLen = udpLen - 8;

                    _ = _udpRouter.RouteOutboundUdpPacketAsync(
                        packet, payloadOffset, payloadLen, srcIp, srcPort, dstIp, dstPort, flowKey);

                    return true; // Intercepted & routed over VPN (Fail-Closed: never touches physical)
                }

                // Resolve owning PID for socket
                int? pid = _identityResolver.FindPidForUdpSocket(srcIp, srcPort);
                if (pid.HasValue && _identityResolver.IsTargetProcess(pid.Value))
                {
                    Interlocked.Increment(ref _targetUdpDetected);
                    Interlocked.Increment(ref _targetUdpProxied);

                    var newFlow = new FlowEntry
                    {
                        Key = flowKey,
                        Pid = pid.Value,
                        IsTargetFlow = true
                    };
                    _flowTable.AddOrUpdate(flowKey, newFlow);

                    int payloadOffset = ipHdrLen + 8;
                    int payloadLen = udpLen - 8;

                    _ = _udpRouter.RouteOutboundUdpPacketAsync(
                        packet, payloadOffset, payloadLen, srcIp, srcPort, dstIp, dstPort, flowKey);

                    return true; // Intercepted
                }
                else if (pid.HasValue)
                {
                    // Non-target flow: remember so we don't re-query PID table for subsequent packets
                    _flowTable.AddOrUpdate(flowKey, new FlowEntry { Key = flowKey, Pid = pid.Value, IsTargetFlow = false });
                    return false; // Direct physical
                }
            }
        }
        else if (proto == 6) // TCP
        {
            if (WinDivertNative.ParseTcpHeader(packet, ipHdrLen, length, out ushort srcPort, out ushort dstPort, out uint seq, out uint ack, out byte flags, out int tcpHdrLen))
            {
                var flowKey = FlowKey.FromEndpoints(6, srcIp, srcPort, dstIp, dstPort);

                if (_flowTable.TryGetFlow(flowKey, out var flow) && flow != null)
                {
                    if (!flow.IsTargetFlow) return false;

                    flow.Touch(bytesSent: length - (ipHdrLen + tcpHdrLen));
                    Interlocked.Increment(ref _targetTcpProxied);

                    _tcpRouter.RegisterTargetMapping(srcPort, dstIp, dstPort);
                    WinDivertNative.ModifyIPv4TcpDestination(packet, length, ipHdrLen, IPAddress.Loopback, (ushort)_tcpRouter.ListenPort, ref addr);
                    addr.Outbound = 0;
                    WinDivertNative.WinDivertSend(_divertHandle, packet, (uint)length, out _, ref addr);
                    return true; // Handled! (Diverted to local TCP proxy)
                }

                int? pid = _identityResolver.FindPidForTcpSocket(srcIp, srcPort, dstIp, dstPort);
                if (pid.HasValue && _identityResolver.IsTargetProcess(pid.Value))
                {
                    Interlocked.Increment(ref _targetTcpDetected);
                    Interlocked.Increment(ref _targetTcpProxied);

                    _flowTable.AddOrUpdate(flowKey, new FlowEntry
                    {
                        Key = flowKey,
                        Pid = pid.Value,
                        IsTargetFlow = true
                    });

                    _tcpRouter.RegisterTargetMapping(srcPort, dstIp, dstPort);
                    WinDivertNative.ModifyIPv4TcpDestination(packet, length, ipHdrLen, IPAddress.Loopback, (ushort)_tcpRouter.ListenPort, ref addr);
                    addr.Outbound = 0;
                    WinDivertNative.WinDivertSend(_divertHandle, packet, (uint)length, out _, ref addr);
                    return true; // Handled!
                }
                else if (pid.HasValue)
                {
                    _flowTable.AddOrUpdate(flowKey, new FlowEntry
                    {
                        Key = flowKey,
                        Pid = pid.Value,
                        IsTargetFlow = false
                    });
                    return false; // Direct physical
                }
            }
        }

        return false;
    }

    // ======================================================================
    // SOCKET OBSERVER LOOP (FAST PID ATTRIBUTION VIA WINDIVERT SOCKET LAYER)
    // ======================================================================

    private void SocketObserverLoopAsync(CancellationToken ct)
    {
        byte[] dummy = new byte[128];
        var addr = new WinDivertNative.WINDIVERT_ADDRESS();

        while (!ct.IsCancellationRequested && _socketObserverHandle != IntPtr.Zero && _socketObserverHandle != new IntPtr(-1))
        {
            if (!WinDivertNative.WinDivertRecv(_socketObserverHandle, dummy, (uint)dummy.Length, out _, ref addr))
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 995 || err == 6) break;
                continue;
            }

            if (addr.Layer == (byte)WinDivertNative.WINDIVERT_LAYER.WINDIVERT_LAYER_SOCKET)
            {
                uint pid = addr.Data.Socket.ProcessId;
                ushort localPort = addr.Data.Socket.LocalPort;
                ushort remotePort = addr.Data.Socket.RemotePort;
                byte proto = addr.Data.Socket.Protocol;

                if (pid > 0 && localPort > 0 && _identityResolver.IsTargetProcess((int)pid))
                {
                    _identityResolver.RegisterPid((int)pid, $"TargetProc_{pid}", $"TargetProc_{pid}.exe");

                    if (remotePort > 0)
                    {
                        var localIp = new IPAddress(BitConverter.GetBytes(addr.Data.Socket.LocalAddr0));
                        var remoteIp = new IPAddress(BitConverter.GetBytes(addr.Data.Socket.RemoteAddr0));
                        var key = FlowKey.FromEndpoints(proto, localIp, localPort, remoteIp, remotePort);

                        _flowTable.AddOrUpdate(key, new FlowEntry
                        {
                            Key = key,
                            Pid = (int)pid,
                            IsTargetFlow = true
                        });
                    }
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
