using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.State;

public enum TunnelHealthState
{
    Healthy,
    Degraded,
    Recovering,
    Unavailable
}

public class HealthReport
{
    public TunnelHealthState State { get; set; } = TunnelHealthState.Healthy;
    public int? MedianLatencyMs { get; set; } // Tunneled Data-Plane / Target Route RTT
    public int? VpnDataPlaneRttMs { get; set; }
    public int? TargetRouteRttMs { get; set; }
    public int? ControlEndpointRttMs { get; set; }
    public bool IsTunneledDataPlaneVerified { get; set; }
    public int ProbesTotal { get; set; }
    public int ProbesSuccessful { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double PacketLossPercent => ProbesTotal > 0 ? ((ProbesTotal - ProbesSuccessful) / (double)ProbesTotal) * 100.0 : 0.0;
    public string Message { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public class HealthChangedEventArgs : EventArgs
{
    public HealthReport Report { get; }

    public HealthChangedEventArgs(HealthReport report)
    {
        Report = report;
    }
}

public class TunnelHealthMonitor : IDisposable
{
    private readonly IAppLogger _logger;
    private readonly string[] _probeTargets;
    private readonly int _probeIntervalMs;
    private readonly int _probeTimeoutMs;
    private readonly int _maxRecoveryAttempts;

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private readonly object _lock = new();

    private TunnelHealthState _currentState = TunnelHealthState.Healthy;
    private int _consecutiveFailures;
    private int _recoveryAttempts;
    private int? _lastKnownGoodLatencyMs;

    public int ProxyPort { get; set; } = 15888;
    public string? ControlEndpointHost { get; set; }
    public int ControlEndpointPort { get; set; } = 443;

    public TunnelHealthState CurrentState
    {
        get { lock (_lock) { return _currentState; } }
    }

    public int? LastKnownGoodLatencyMs
    {
        get { lock (_lock) { return _lastKnownGoodLatencyMs; } }
    }

    public event EventHandler<HealthChangedEventArgs>? HealthChanged;
    public Func<Task<bool>>? OnPerformRecoveryAsync { get; set; }

    public TunnelHealthMonitor(
        IAppLogger logger,
        string[]? probeTargets = null,
        int probeIntervalMs = 6000,
        int probeTimeoutMs = 1500,
        int maxRecoveryAttempts = 2,
        int proxyPort = 15888)
    {
        _logger = logger;
        _probeTargets = probeTargets ?? new[] { "1.1.1.1", "8.8.8.8", "1.0.0.1" };
        _probeIntervalMs = Math.Max(3000, probeIntervalMs);
        _probeTimeoutMs = Math.Max(500, probeTimeoutMs);
        _maxRecoveryAttempts = maxRecoveryAttempts;
        ProxyPort = proxyPort;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public void StartMonitoring(string? targetEndpoint = null, int proxyPort = 15888)
    {
        lock (_lock)
        {
            StopMonitoring();

            _monitorCts = new CancellationTokenSource();
            _currentState = TunnelHealthState.Healthy;
            _consecutiveFailures = 0;
            _recoveryAttempts = 0;
            ProxyPort = proxyPort;

            if (!string.IsNullOrWhiteSpace(targetEndpoint))
            {
                var parts = targetEndpoint.Split(':');
                ControlEndpointHost = parts[0];
                if (parts.Length > 1 && int.TryParse(parts[1], out int p))
                {
                    ControlEndpointPort = p;
                }
            }

            _logger.Info($"[TunnelHealthMonitor] Starting health monitor (Interval: {_probeIntervalMs}ms, ProxyPort: {ProxyPort}, ControlEndpoint: {ControlEndpointHost}:{ControlEndpointPort}, DataPlaneTargets: {string.Join(", ", _probeTargets)})...");
            _monitorTask = Task.Run(() => MonitorLoopAsync(_probeTargets, _monitorCts.Token));
        }
    }

    public void StopMonitoring()
    {
        lock (_lock)
        {
            if (_monitorCts != null)
            {
                try
                {
                    _monitorCts.Cancel();
                }
                catch { }
                _monitorCts.Dispose();
                _monitorCts = null;
            }

            _monitorTask = null;
            _currentState = TunnelHealthState.Healthy;
            _consecutiveFailures = 0;
            _recoveryAttempts = 0;
            _logger.Info("[TunnelHealthMonitor] Health monitoring stopped.");
        }
    }

    private async Task MonitorLoopAsync(string[] targets, CancellationToken ct)
    {
        // Initial delay before first health probe
        try
        {
            await Task.Delay(2000, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var report = await ExecuteHealthProbesAsync(targets, ct);
                await EvaluateHealthReportAsync(report, ct);

                await Task.Delay(_probeIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[TunnelHealthMonitor] Error during health check: {ex.Message}");
                try
                {
                    await Task.Delay(3000, ct);
                }
                catch { break; }
            }
        }
    }

    public async Task<HealthReport> ExecuteHealthProbesAsync(string[] targets, CancellationToken ct = default)
    {
        var successfulRtts = new List<int>();
        var totalProbes = 3;

        // 1. Optional diagnostic probe to control endpoint (over physical path)
        int? controlRtt = null;
        if (!string.IsNullOrEmpty(ControlEndpointHost))
        {
            controlRtt = await ProbeControlEndpointAsync(ControlEndpointHost, ControlEndpointPort, _probeTimeoutMs, ct);
        }

        // 2. Real data-plane probes traversing local proxy / VPN transport
        for (int i = 0; i < totalProbes; i++)
        {
            if (ct.IsCancellationRequested) break;

            var target = targets[i % targets.Length];
            var rtt = await ProbeSocks5DataPlaneAsync("127.0.0.1", ProxyPort, target, 80, _probeTimeoutMs, ct);
            if (rtt.HasValue && rtt.Value > 0 && rtt.Value < _probeTimeoutMs)
            {
                successfulRtts.Add(rtt.Value);
            }
            
            // Short spacing between sample probes
            if (i < totalProbes - 1)
            {
                try { await Task.Delay(150, ct); } catch { }
            }
        }

        int? medianDataPlaneLatency = CalculateMedianLatency(successfulRtts);
        if (medianDataPlaneLatency.HasValue)
        {
            lock (_lock)
            {
                _lastKnownGoodLatencyMs = medianDataPlaneLatency;
            }
        }

        var report = new HealthReport
        {
            ProbesTotal = totalProbes,
            ProbesSuccessful = successfulRtts.Count,
            ControlEndpointRttMs = controlRtt,
            VpnDataPlaneRttMs = medianDataPlaneLatency,
            TargetRouteRttMs = medianDataPlaneLatency,
            MedianLatencyMs = medianDataPlaneLatency, // Authoritative tunneled latency only!
            IsTunneledDataPlaneVerified = medianDataPlaneLatency.HasValue,
            TimestampUtc = DateTime.UtcNow
        };

        return report;
    }

    public static int? CalculateMedianLatency(IEnumerable<int> samples)
    {
        var list = samples.Where(s => s >= 0).ToList();
        if (list.Count == 0) return null;
        list.Sort();
        int mid = list.Count / 2;
        return (list.Count % 2 != 0) ? list[mid] : (list[mid - 1] + list[mid]) / 2;
    }

    public static async Task<int?> ProbeSocks5DataPlaneAsync(
        string proxyHost,
        int proxyPort,
        string targetHost,
        int targetPort,
        int timeoutMs,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeoutMs);

        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.NoDelay = true;

            await sock.ConnectAsync(proxyHost, proxyPort, linkedCts.Token);

            // 1. SOCKS5 Greeting: [Version=5, NMethods=1, Method=0 (No Auth)]
            await sock.SendAsync(new byte[] { 0x05, 0x01, 0x00 }, SocketFlags.None, linkedCts.Token);
            byte[] authResponse = new byte[2];
            int authRead = await sock.ReceiveAsync(authResponse, SocketFlags.None, linkedCts.Token);
            if (authRead < 2 || authResponse[0] != 0x05 || authResponse[1] != 0x00)
            {
                return null;
            }

            // 2. SOCKS5 Connect Request
            byte[] request;
            if (IPAddress.TryParse(targetHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                request = new byte[10];
                request[0] = 0x05;
                request[1] = 0x01;
                request[2] = 0x00;
                request[3] = 0x01;
                Buffer.BlockCopy(ip.GetAddressBytes(), 0, request, 4, 4);
                request[8] = (byte)(targetPort >> 8);
                request[9] = (byte)(targetPort & 0xFF);
            }
            else
            {
                byte[] domainBytes = System.Text.Encoding.ASCII.GetBytes(targetHost);
                request = new byte[7 + domainBytes.Length];
                request[0] = 0x05;
                request[1] = 0x01;
                request[2] = 0x00;
                request[3] = 0x03;
                request[4] = (byte)domainBytes.Length;
                Buffer.BlockCopy(domainBytes, 0, request, 5, domainBytes.Length);
                request[5 + domainBytes.Length] = (byte)(targetPort >> 8);
                request[6 + domainBytes.Length] = (byte)(targetPort & 0xFF);
            }

            var startDataPlane = sw.ElapsedMilliseconds;
            await sock.SendAsync(request, SocketFlags.None, linkedCts.Token);

            byte[] reply = new byte[10];
            int replyRead = await sock.ReceiveAsync(reply, SocketFlags.None, linkedCts.Token);
            if (replyRead >= 2 && reply[1] == 0x00)
            {
                var rtt = (int)(sw.ElapsedMilliseconds - startDataPlane);
                return Math.Max(1, rtt);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    public static async Task<int?> ProbeControlEndpointAsync(
        string host,
        int port,
        int timeoutMs,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeoutMs);

        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.NoDelay = true;
            await sock.ConnectAsync(host, port, linkedCts.Token);
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, timeoutMs);
                if (reply.Status == IPStatus.Success && reply.RoundtripTime < timeoutMs)
                {
                    return (int)reply.RoundtripTime;
                }
            }
            catch { }
        }

        return null;
    }

    private async Task EvaluateHealthReportAsync(HealthReport report, CancellationToken ct)
    {
        TunnelHealthState nextState;
        string detailMessage;

        lock (_lock)
        {
            if (report.ProbesSuccessful >= 2) // At least 2 of 3 tunneled probes passed
            {
                _consecutiveFailures = 0;
                _recoveryAttempts = 0;
                nextState = TunnelHealthState.Healthy;
                detailMessage = $"Connected (Tunnel Latency: {report.MedianLatencyMs} ms)";
            }
            else if (report.ProbesSuccessful == 1) // Degraded
            {
                _consecutiveFailures++;
                nextState = TunnelHealthState.Degraded;
                detailMessage = report.MedianLatencyMs.HasValue
                    ? $"Connection degraded (Tunnel Latency: {report.MedianLatencyMs} ms)"
                    : "Connection degraded (Data plane loss)";
            }
            else // 0 of 3 passed: All tunneled probes timed out / failed
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 3)
                {
                    nextState = TunnelHealthState.Unavailable;
                    nextState = TunnelHealthState.Unavailable;
                    detailMessage = "Connection timeout (Data plane unreachable)";
                }
                else
                {
                    nextState = TunnelHealthState.Degraded;
                    detailMessage = "Connection degraded (Tunneled probes lost)";
                }
            }

            report.State = nextState;
            report.ConsecutiveFailures = _consecutiveFailures;
            report.Message = detailMessage;

            if (_currentState != nextState)
            {
                _logger.Info($"[TunnelHealthMonitor] Tunnel health changed {_currentState} -> {nextState} (Loss: {report.PacketLossPercent:F0}%, Tunnel Latency: {report.MedianLatencyMs?.ToString() ?? "N/A"} ms, Control RTT: {report.ControlEndpointRttMs?.ToString() ?? "N/A"} ms, ConsecutiveFailures: {_consecutiveFailures})");
                _currentState = nextState;
            }
        }

        HealthChanged?.Invoke(this, new HealthChangedEventArgs(report));

        // Check if bounded self-recovery is needed
        if (nextState == TunnelHealthState.Unavailable && OnPerformRecoveryAsync != null)
        {
            await TriggerBoundedRecoveryAsync(ct);
        }
    }

    private async Task TriggerBoundedRecoveryAsync(CancellationToken ct)
    {
        int attempt;
        lock (_lock)
        {
            if (_recoveryAttempts >= _maxRecoveryAttempts)
            {
                _logger.Warning($"[TunnelHealthMonitor] Max recovery attempts ({_maxRecoveryAttempts}) reached for this incident. Awaiting manual operator action.");
                return;
            }

            _recoveryAttempts++;
            attempt = _recoveryAttempts;
            _currentState = TunnelHealthState.Recovering;
        }

        _logger.Info($"[TunnelHealthMonitor] Initiating automatic recovery attempt {attempt}/{_maxRecoveryAttempts}...");
        
        var recoveringReport = new HealthReport
        {
            State = TunnelHealthState.Recovering,
            ConsecutiveFailures = _consecutiveFailures,
            Message = $"Reconnecting (Attempt {attempt}/{_maxRecoveryAttempts})..."
        };
        HealthChanged?.Invoke(this, new HealthChangedEventArgs(recoveringReport));

        try
        {
            var recovered = await OnPerformRecoveryAsync!();
            if (recovered)
            {
                _logger.Info($"[TunnelHealthMonitor] Recovery attempt {attempt} succeeded. Tunnel re-established.");
                lock (_lock)
                {
                    _consecutiveFailures = 0;
                    _currentState = TunnelHealthState.Healthy;
                }
            }
            else
            {
                _logger.Warning($"[TunnelHealthMonitor] Recovery attempt {attempt} failed.");
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"[TunnelHealthMonitor] Exception during recovery attempt {attempt}", ex);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _logger.Info("[TunnelHealthMonitor] System network address changed event detected. Re-verifying tunnel health...");
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _logger.Info($"[TunnelHealthMonitor] Network availability changed (IsAvailable: {e.IsAvailable}).");
    }

    public void Dispose()
    {
        StopMonitoring();
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
