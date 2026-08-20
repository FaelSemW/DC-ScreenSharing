using System.Net;

namespace DC_ScreenSharing.Networking.ProcessIsolation;

public class ProcessIsolationOptions
{
    public List<string> TargetProcessNames { get; set; } = new() { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe" };
    public int VpnInterfaceIndex { get; set; } = 0;
    public IPAddress? VpnInterfaceIp { get; set; }
    public IPAddress? VpnServerIp { get; set; }
    public ushort VpnServerPort { get; set; } = 0;
    public string TransportType { get; set; } = "OpenVPN";
    public bool BlockIpv6ForTarget { get; set; } = true;
}

public class ProcessIsolationStats
{
    public bool IsRunning { get; set; }
    public int ActiveTcpFlows { get; set; }
    public int ActiveUdpFlows { get; set; }
    public long TotalTcpBytesSent { get; set; }
    public long TotalTcpBytesReceived { get; set; }
    public long TotalUdpBytesSent { get; set; }
    public long TotalUdpBytesReceived { get; set; }
    public int TrackedProcessesCount { get; set; }
    public string TransportName { get; set; } = "OpenVPN";
    public int VpnInterfaceIndex { get; set; }
    public string? VpnInterfaceIp { get; set; }
    public double UptimeSeconds { get; set; }
}

public interface IProcessIsolationEngine : IAsyncDisposable
{
    bool IsRunning { get; }
    Task StartAsync(ProcessIsolationOptions options, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    void UpdateTargetProcesses(IEnumerable<string> targetProcessNames);
    void SetInterfaceBinding(int interfaceIndex, IPAddress interfaceIp);
    ProcessIsolationStats GetStats();
}
