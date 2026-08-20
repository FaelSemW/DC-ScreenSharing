using System.Text.Json.Serialization;

namespace DCSS.ProfileCollector.Models;

public static class ProviderConstants
{
    public const string ProtonVpn = "Proton VPN";
    public const string VpnBook = "VPNBook";
}

public class CollectorServer
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("countryName")]
    public string CountryName { get; set; } = string.Empty;

    public override string ToString() => !string.IsNullOrEmpty(Hostname) ? $"{Name} ({Hostname})" : Name;
}

public class CollectorRegion
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<CollectorServer> Servers { get; set; } = new();

    public override string ToString() => $"{DisplayName} ({Code})";
}

// Legacy aliases for backward compatibility with existing code
public class VpnBookServer : CollectorServer { }
public class VpnBookRegion : CollectorRegion { }

public class PortOption
{
    public string Port { get; set; } = "51820";
    public string Description { get; set; } = "51820 (WireGuard Default)";

    public override string ToString() => Description;
}

public class ProtonOptions
{
    public string Platform { get; set; } = "Windows";
    public string NetShield { get; set; } = "Block malware only";
    public bool ModerateNat { get; set; } = false;
    public bool NatPmp { get; set; } = false;
    public bool VpnAccelerator { get; set; } = true;
}

public class ProfileGenerationOptions
{
    public string Provider { get; set; } = ProviderConstants.ProtonVpn;
    public CollectorRegion Region { get; set; } = new();
    public string ServerMode { get; set; } = "Automatic";
    public string? SpecificServerId { get; set; }
    public string Port { get; set; } = "51820";
    public string ConfigurationName { get; set; } = "DCSS-US-001";
    public ProtonOptions ProtonSettings { get; set; } = new();
}

public class ProviderProfileResult
{
    public bool Success { get; set; }
    public string ConfigContent { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public DateTime? ExpiresAtUtc { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public bool RequiresOperatorAttention { get; set; }
    public string OperatorAttentionReason { get; set; } = string.Empty;
}

public class ProfileResultItem
{
    public string Filename { get; set; } = string.Empty;
    public string Provider { get; set; } = ProviderConstants.ProtonVpn;
    public string Region { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string Status { get; set; } = "Ready";
    public DateTime? ExpiresAtUtc { get; set; }
    public string ExpirationDisplay => ExpiresAtUtc.HasValue ? $"Expires: {ExpiresAtUtc.Value:yyyy-MM-dd}" : "Expires: None (Proton Standard)";
    public string StatusDetail { get; set; } = string.Empty;
    public string? DerivedPublicKeyHash { get; set; }
}

public class InventoryItem
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = ProviderConstants.ProtonVpn;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;

    [JsonPropertyName("generatedUtc")]
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("validationStatus")]
    public string ValidationStatus { get; set; } = "Valid";

    [JsonPropertyName("derivedPublicIdentityHash")]
    public string DerivedPublicIdentityHash { get; set; } = string.Empty;

    [JsonPropertyName("expiresAtUtc")]
    public DateTime? ExpiresAtUtc { get; set; }
}

public class InventoryDatabase
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("lastUpdatedUtc")]
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("items")]
    public List<InventoryItem> Items { get; set; } = new();
}

public class MultiRegionPlanItem
{
    public string RegionCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Quantity { get; set; } = 10;
    public string Status { get; set; } = "Pending";
}
