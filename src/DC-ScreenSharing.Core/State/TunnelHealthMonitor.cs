using System.Net.NetworkInformation;
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
    public int? MedianLatencyMs { get; set; }
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
        int maxRecoveryAttempts = 2)
    {
        _logger = logger;
        _probeTargets = probeTargets ?? new[] { "1.1.1.1", "8.8.8.8", "1.0.0.1" };
        _probeIntervalMs = Math.Max(3000, probeIntervalMs);
        _probeTimeoutMs = Math.Max(500, probeTimeoutMs);
        _maxRecoveryAttempts = maxRecoveryAttempts;

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public void StartMonitoring(string? targetEndpoint = null)
    {
        lock (_lock)
        {
            StopMonitoring();

            _monitorCts = new CancellationTokenSource();
            _currentState = TunnelHealthState.Healthy;
            _consecutiveFailures = 0;
            _recoveryAttempts = 0;

            var targets = new List<string>(_probeTargets);
            if (!string.IsNullOrWhiteSpace(targetEndpoint) && !targets.Contains(targetEndpoint))
            {
                targets.Insert(0, targetEndpoint);
            }

            _logger.Info($"[TunnelHealthMonitor] Starting health monitor (Interval: {_probeIntervalMs}ms, Timeout: {_probeTimeoutMs}ms, Targets: {string.Join(", ", targets)})...");
            _monitorTask = Task.Run(() => MonitorLoopAsync(targets.ToArray(), _monitorCts.Token));
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

        for (int i = 0; i < totalProbes; i++)
        {
            if (ct.IsCancellationRequested) break;

            var target = targets[i % targets.Length];
            var rtt = await ProbeSingleAsync(target, _probeTimeoutMs, ct);
            if (rtt.HasValue && rtt.Value >= 0 && rtt.Value < _probeTimeoutMs)
            {
                successfulRtts.Add(rtt.Value);
            }
            
            // Short spacing between sample probes
            if (i < totalProbes - 1)
            {
                try { await Task.Delay(150, ct); } catch { }
            }
        }

        int? medianLatency = null;
        if (successfulRtts.Count > 0)
        {
            successfulRtts.Sort();
            int mid = successfulRtts.Count / 2;
            medianLatency = (successfulRtts.Count % 2 != 0) 
                ? successfulRtts[mid] 
                : (successfulRtts[mid - 1] + successfulRtts[mid]) / 2;
            
            lock (_lock)
            {
                _lastKnownGoodLatencyMs = medianLatency;
            }
        }

        var report = new HealthReport
        {
            ProbesTotal = totalProbes,
            ProbesSuccessful = successfulRtts.Count,
            MedianLatencyMs = medianLatency,
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

    private async Task<int?> ProbeSingleAsync(string host, int timeoutMs, CancellationToken ct)
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

        return null;
    }

    private async Task EvaluateHealthReportAsync(HealthReport report, CancellationToken ct)
    {
        TunnelHealthState nextState;
        string detailMessage;

        lock (_lock)
        {
            if (report.ProbesSuccessful >= 2) // At least 2 of 3 probes passed
            {
                _consecutiveFailures = 0;
                _recoveryAttempts = 0;
                nextState = TunnelHealthState.Healthy;
                detailMessage = $"Connected (Latency: {report.MedianLatencyMs} ms)";
            }
            else if (report.ProbesSuccessful == 1) // Degraded: 1 of 3 passed
            {
                _consecutiveFailures++;
                nextState = TunnelHealthState.Degraded;
                detailMessage = report.MedianLatencyMs.HasValue
                    ? $"Connection degraded (Latency: {report.MedianLatencyMs} ms)"
                    : "Connection degraded";
            }
            else // 0 of 3 passed: All probes timed out / failed
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= 3)
                {
                    nextState = TunnelHealthState.Unavailable;
                    detailMessage = "Connection timeout";
                }
                else
                {
                    nextState = TunnelHealthState.Degraded;
                    detailMessage = "Connection degraded (Probes lost)";
                }
            }

            report.State = nextState;
            report.ConsecutiveFailures = _consecutiveFailures;
            report.Message = detailMessage;

            if (_currentState != nextState)
            {
                _logger.Info($"[TunnelHealthMonitor] Tunnel health changed {_currentState} -> {nextState} (Loss: {report.PacketLossPercent:F0}%, Latency: {report.MedianLatencyMs?.ToString() ?? "Timeout"} ms, ConsecutiveFailures: {_consecutiveFailures})");
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
