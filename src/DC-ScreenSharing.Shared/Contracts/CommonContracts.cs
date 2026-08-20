using System.Text.Json.Serialization;

namespace DCScreenSharing.Shared.Contracts;

public enum IpcCommand
{
    Ping = 0,
    GetStatus = 1,
    StartTunnel = 2,
    StopTunnel = 3,
    GetDiagnostics = 4,
    CleanupOrphaned = 5,
    ValidateConfig = 6
}

public class IpcMessage
{
    [JsonPropertyName("command")]
    public IpcCommand Command { get; set; }

    [JsonPropertyName("payloadJson")]
    public string PayloadJson { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
}

public class TunnelConfiguration
{
    [JsonPropertyName("serverId")]
    public string ServerId { get; set; } = string.Empty;

    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "WIREGUARD";

    [JsonPropertyName("openvpnProfileJson")]
    public string? OpenVpnProfileJson { get; set; }

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 51820;

    [JsonPropertyName("address")]
    public string Address
    {
        get => Addresses.Count > 0 ? string.Join(", ", Addresses) : _address;
        set
        {
            _address = value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value) && (Addresses == null || Addresses.Count == 0))
            {
                Addresses = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
        }
    }
    private string _address = "10.8.0.2/32";

    [JsonPropertyName("addresses")]
    public List<string> Addresses { get; set; } = new();

    [JsonPropertyName("dns")]
    public string Dns
    {
        get => DnsServers.Count > 0 ? string.Join(", ", DnsServers) : _dns;
        set
        {
            _dns = value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value) && (DnsServers == null || DnsServers.Count == 0))
            {
                DnsServers = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
        }
    }
    private string _dns = "1.1.1.1, 8.8.8.8";

    [JsonPropertyName("dnsServers")]
    public List<string> DnsServers { get; set; } = new();

    [JsonPropertyName("privateKey")]
    public string PrivateKey { get; set; } = string.Empty;

    [JsonPropertyName("peerPublicKey")]
    public string PeerPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("allowedIps")]
    public string AllowedIps
    {
        get => AllowedIpsList.Count > 0 ? string.Join(", ", AllowedIpsList) : _allowedIps;
        set
        {
            _allowedIps = value ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(value) && (AllowedIpsList == null || AllowedIpsList.Count == 0))
            {
                AllowedIpsList = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
        }
    }
    private string _allowedIps = "0.0.0.0/0, ::/0";

    [JsonPropertyName("allowedIpsList")]
    public List<string> AllowedIpsList { get; set; } = new();

    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = Constants.DefaultInterfaceMtu;

    [JsonPropertyName("persistentKeepalive")]
    public int PersistentKeepalive { get; set; } = 25;

    [JsonPropertyName("allowedApps")]
    public List<string> AllowedApps { get; set; } = new()
    {
        "Discord.exe",
        "DiscordCanary.exe",
        "DiscordPTB.exe",
        "DiscordDevelopment.exe"
    };

    [JsonPropertyName("discordExecutablePath")]
    public string? DiscordExecutablePath { get; set; }

    [JsonPropertyName("discordProcessIds")]
    public List<int> DiscordProcessIds { get; set; } = new();
}

public class TunnelResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("interfaceName")]
    public string? InterfaceName { get; set; }
}

public class ServiceStatusResponse
{
    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; } = true;

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("activeServerId")]
    public string? ActiveServerId { get; set; }

    [JsonPropertyName("activeServerName")]
    public string? ActiveServerName { get; set; }

    [JsonPropertyName("connectedSinceUtc")]
    public DateTime? ConnectedSinceUtc { get; set; }

    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; set; } = "1.0.6";

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

public class DiagnosticsData
{
    [JsonPropertyName("serviceVersion")]
    public string ServiceVersion { get; set; } = string.Empty;

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [JsonPropertyName("is64Bit")]
    public bool Is64Bit { get; set; }

    [JsonPropertyName("tunnelActive")]
    public bool TunnelActive { get; set; }

    [JsonPropertyName("activeServerId")]
    public string? ActiveServerId { get; set; }

    [JsonPropertyName("serviceUptimeSeconds")]
    public double ServiceUptimeSeconds { get; set; }

    [JsonPropertyName("networkInterfaces")]
    public List<string> NetworkInterfaces { get; set; } = new();

    [JsonPropertyName("sanitizedRecentLogs")]
    public List<string> SanitizedRecentLogs { get; set; } = new();
}
