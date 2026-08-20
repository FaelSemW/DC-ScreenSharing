using System.Text.Json.Serialization;

namespace DCScreenSharing.Core.Profiles;

public class ServerEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("pingMs")]
    public int? PingMs { get; set; }
}

public class ServerCatalog
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("generation")]
    public int Generation { get; set; } = 1;

    [JsonPropertyName("publishedAtUtc")]
    public DateTime PublishedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("servers")]
    public List<ServerEntry> Servers { get; set; } = new();
}

public class WireGuardProfileConfig
{
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
            if (!string.IsNullOrWhiteSpace(value) && Addresses.Count == 0)
            {
                Addresses = WireGuardConfParser.ParseCidrList(value);
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
            if (!string.IsNullOrWhiteSpace(value) && DnsServers.Count == 0)
            {
                DnsServers = WireGuardConfParser.ParseDnsList(value);
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
            if (!string.IsNullOrWhiteSpace(value) && AllowedIpsList.Count == 0)
            {
                AllowedIpsList = WireGuardConfParser.ParseCidrList(value);
            }
        }
    }
    private string _allowedIps = "0.0.0.0/0, ::/0";

    [JsonPropertyName("allowedIpsList")]
    public List<string> AllowedIpsList { get; set; } = new();

    [JsonPropertyName("mtu")]
    public int Mtu { get; set; } = 1420;

    [JsonPropertyName("persistentKeepalive")]
    public int PersistentKeepalive { get; set; } = 25;
}

public class ServerProfile
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("serverId")]
    public string ServerId { get; set; } = string.Empty;

    [JsonPropertyName("generation")]
    public int Generation { get; set; } = 1;

    [JsonPropertyName("issuedAt")]
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("wireguard")]
    public WireGuardProfileConfig Wireguard { get; set; } = new();
}

public class SignedManifest
{
    [JsonPropertyName("catalogJson")]
    public string CatalogJson { get; set; } = string.Empty;

    [JsonPropertyName("signatureBase64")]
    public string SignatureBase64 { get; set; } = string.Empty;

    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTime PublishedAtUtc { get; set; }

    [JsonPropertyName("profiles")]
    public Dictionary<string, string> ProfilePayloads { get; set; } = new(); // serverId -> JSON

    [JsonPropertyName("profileSignatures")]
    public Dictionary<string, string> ProfileSignatures { get; set; } = new(); // serverId -> Signature
}
