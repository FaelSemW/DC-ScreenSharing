using System.Text.Json.Serialization;

namespace DCSS.ProfileCollector.Models;

public class VpnBookServer
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

    public override string ToString() => $"{Name} ({Hostname})";
}

public class VpnBookRegion
{
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<VpnBookServer> Servers { get; set; } = new();

    public override string ToString() => $"{DisplayName} ({Code})";
}

public class PortOption
{
    public string Port { get; set; } = "443";
    public string Description { get; set; } = "443 (HTTPS) - Best for bypassing firewalls";

    public override string ToString() => Description;
}

public class ProfileResultItem
{
    public string Filename { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string Status { get; set; } = "Ready";
    public DateTime? ExpiresAtUtc { get; set; }
    public string ExpirationDisplay => ExpiresAtUtc.HasValue ? $"Expires: {ExpiresAtUtc.Value:yyyy-MM-dd}" : "Expires: 7 days";
    public string StatusDetail { get; set; } = string.Empty;
    public string? DerivedPublicKeyHash { get; set; }
}

public class InventoryItem
{
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
