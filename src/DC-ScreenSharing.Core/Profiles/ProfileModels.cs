using System.Text.Json.Serialization;

namespace DCScreenSharing.Core.Profiles;

public static class VpnProtocol
{
    public const string WireGuard = "WIREGUARD";
    public const string OpenVpn = "OPENVPN";

    public static bool IsOpenVpn(string? protocol) =>
        string.Equals(protocol, OpenVpn, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(protocol, "openvpn", StringComparison.OrdinalIgnoreCase);

    public static bool IsWireGuard(string? protocol) =>
        string.IsNullOrEmpty(protocol) ||
        string.Equals(protocol, WireGuard, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(protocol, "wireguard", StringComparison.OrdinalIgnoreCase);

    public static bool IsStrictWireGuard(string? protocol) =>
        string.Equals(protocol, WireGuard, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(protocol, "wireguard", StringComparison.OrdinalIgnoreCase);

    public static bool IsValidProtocol(string? protocol) =>
        !string.IsNullOrWhiteSpace(protocol) &&
        (string.Equals(protocol, OpenVpn, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(protocol, "openvpn", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(protocol, WireGuard, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(protocol, "wireguard", StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? protocol) =>
        IsOpenVpn(protocol) ? OpenVpn : (IsStrictWireGuard(protocol) ? WireGuard : (string.IsNullOrEmpty(protocol) ? WireGuard : protocol?.ToUpperInvariant() ?? WireGuard));
}

public class ServerEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Custom";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = VpnProtocol.WireGuard;

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

public class OpenVpnRemoteEndpoint
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 1194;

    [JsonPropertyName("proto")]
    public string Proto { get; set; } = "udp"; // "udp", "tcp", "udp4", "udp6", "tcp4", "tcp6"
}

public class OpenVpnProfileConfig
{
    [JsonPropertyName("remoteEndpoints")]
    public List<OpenVpnRemoteEndpoint> RemoteEndpoints { get; set; } = new();

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "udp"; // "udp" or "tcp"

    [JsonPropertyName("device")]
    public string Device { get; set; } = "tun";

    [JsonPropertyName("cipher")]
    public string Cipher { get; set; } = string.Empty;

    [JsonPropertyName("dataCiphers")]
    public string DataCiphers { get; set; } = string.Empty;

    [JsonPropertyName("dataCiphersFallback")]
    public string DataCiphersFallback { get; set; } = string.Empty;

    [JsonPropertyName("auth")]
    public string Auth { get; set; } = string.Empty;

    [JsonPropertyName("caCert")]
    public string CaCert { get; set; } = string.Empty;

    [JsonPropertyName("clientCert")]
    public string ClientCert { get; set; } = string.Empty;

    [JsonPropertyName("clientKey")]
    public string ClientKey { get; set; } = string.Empty; // Highly sensitive

    [JsonPropertyName("tlsAuthKey")]
    public string TlsAuthKey { get; set; } = string.Empty;

    [JsonPropertyName("tlsCryptKey")]
    public string TlsCryptKey { get; set; } = string.Empty;

    [JsonPropertyName("tlsCryptV2Key")]
    public string TlsCryptV2Key { get; set; } = string.Empty;

    [JsonPropertyName("keyDirection")]
    public string? KeyDirection { get; set; }

    [JsonPropertyName("authUserPass")]
    public bool AuthUserPass { get; set; }

    [JsonPropertyName("credentialSetId")]
    public string? CredentialSetId { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("encryptedPassword")]
    public string? EncryptedPassword { get; set; }

    [JsonPropertyName("remoteCertTls")]
    public string? RemoteCertTls { get; set; } = "server";

    [JsonPropertyName("resolvRetry")]
    public string? ResolvRetry { get; set; } = "infinite";

    [JsonPropertyName("nobind")]
    public bool Nobind { get; set; } = true;

    [JsonPropertyName("persistKey")]
    public bool PersistKey { get; set; } = true;

    [JsonPropertyName("persistTun")]
    public bool PersistTun { get; set; } = true;

    [JsonPropertyName("tunMtu")]
    public int? TunMtu { get; set; }

    [JsonPropertyName("mssfix")]
    public int? Mssfix { get; set; }

    [JsonPropertyName("verb")]
    public int Verb { get; set; } = 3;

    [JsonPropertyName("connectRetry")]
    public int? ConnectRetry { get; set; }

    [JsonPropertyName("connectTimeout")]
    public int? ConnectTimeout { get; set; }

    [JsonPropertyName("compress")]
    public string? Compress { get; set; }

    [JsonPropertyName("safeDirectives")]
    public Dictionary<string, string> SafeDirectives { get; set; } = new();

    [JsonPropertyName("rawConfigSafe")]
    public string RawConfigSafe { get; set; } = string.Empty;
}

public class ServerProfile
{
    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("serverId")]
    public string ServerId { get; set; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = VpnProtocol.WireGuard;

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

    [JsonPropertyName("openvpn")]
    public OpenVpnProfileConfig? Openvpn { get; set; }
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
