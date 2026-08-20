using System.Collections.Concurrent;
using System.Net;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public readonly record struct FlowKey(
    byte Protocol,
    uint LocalIp,
    ushort LocalPort,
    uint RemoteIp,
    ushort RemotePort)
{
    public static FlowKey FromEndpoints(byte protocol, IPAddress localIp, ushort localPort, IPAddress remoteIp, ushort remotePort)
    {
        uint lIp = BitConverter.ToUInt32(localIp.GetAddressBytes(), 0);
        uint rIp = BitConverter.ToUInt32(remoteIp.GetAddressBytes(), 0);
        return new FlowKey(protocol, lIp, localPort, rIp, remotePort);
    }
}

public class FlowEntry
{
    public FlowKey Key { get; set; }
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public bool IsTargetFlow { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
    public long BytesSent;
    public long BytesReceived;
    public string State { get; set; } = "ESTABLISHED";
    public object? Context { get; set; }

    public void Touch(long bytesSent = 0, long bytesReceived = 0)
    {
        LastActivityUtc = DateTime.UtcNow;
        if (bytesSent > 0) Interlocked.Add(ref BytesSent, bytesSent);
        if (bytesReceived > 0) Interlocked.Add(ref BytesReceived, bytesReceived);
    }
}

public class FlowMappingTable
{
    private readonly ConcurrentDictionary<FlowKey, FlowEntry> _flows = new();
    private const int MaxFlowsLimit = 10000;

    public int Count => _flows.Count;

    public bool TryGetFlow(FlowKey key, out FlowEntry? entry)
    {
        return _flows.TryGetValue(key, out entry);
    }

    public FlowEntry GetOrAdd(FlowKey key, Func<FlowKey, FlowEntry> factory)
    {
        if (_flows.Count >= MaxFlowsLimit)
        {
            // Immediate emergency purge of oldest 10% when full
            PruneOldest(MaxFlowsLimit / 10);
        }

        return _flows.GetOrAdd(key, factory);
    }

    public void AddOrUpdate(FlowKey key, FlowEntry entry)
    {
        if (_flows.Count >= MaxFlowsLimit)
        {
            PruneOldest(MaxFlowsLimit / 10);
        }

        _flows[key] = entry;
    }

    public bool Remove(FlowKey key, out FlowEntry? entry)
    {
        return _flows.TryRemove(key, out entry);
    }

    public void PruneExpiredFlows(TimeSpan tcpTimeout, TimeSpan udpTimeout)
    {
        var now = DateTime.UtcNow;
        foreach (var kvp in _flows)
        {
            var isTcp = kvp.Key.Protocol == 6; // IPPROTO_TCP
            var timeout = isTcp ? tcpTimeout : udpTimeout;

            if (now - kvp.Value.LastActivityUtc > timeout || kvp.Value.State == "CLOSED")
            {
                if (_flows.TryRemove(kvp.Key, out var removed))
                {
                    if (removed.Context is IDisposable disposable)
                    {
                        try { disposable.Dispose(); } catch { }
                    }
                }
            }
        }
    }

    private void PruneOldest(int countToPrune)
    {
        var oldest = _flows
            .OrderBy(kvp => kvp.Value.LastActivityUtc)
            .Take(countToPrune)
            .ToList();

        foreach (var kvp in oldest)
        {
            if (_flows.TryRemove(kvp.Key, out var removed))
            {
                if (removed.Context is IDisposable disposable)
                {
                    try { disposable.Dispose(); } catch { }
                }
            }
        }
    }

    public (int TcpCount, int UdpCount) GetActiveCounts()
    {
        int tcp = 0;
        int udp = 0;
        foreach (var kvp in _flows)
        {
            if (kvp.Key.Protocol == 6) tcp++;
            else if (kvp.Key.Protocol == 17) udp++;
        }
        return (tcp, udp);
    }

    public void Clear()
    {
        foreach (var kvp in _flows)
        {
            if (kvp.Value.Context is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
        _flows.Clear();
    }
}
