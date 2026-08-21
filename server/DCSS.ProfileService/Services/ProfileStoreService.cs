using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Shared.Logging;

namespace DCSS.ProfileService.Services;

public class GenerationRecord
{
    [JsonPropertyName("generation")]
    public int Generation { get; set; }

    [JsonPropertyName("publishedAtUtc")]
    public DateTime PublishedAtUtc { get; set; }

    [JsonPropertyName("publishedBy")]
    public string PublishedBy { get; set; } = "Maintainer";

    [JsonPropertyName("serverCount")]
    public int ServerCount { get; set; }

    [JsonPropertyName("wireguardCount")]
    public int WireGuardCount { get; set; }

    [JsonPropertyName("openvpnCount")]
    public int OpenVpnCount { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}

public class ActiveGenerationPointer
{
    [JsonPropertyName("activeGeneration")]
    public int ActiveGeneration { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

public class PublicationStatusSummary
{
    [JsonPropertyName("activeGeneration")]
    public int ActiveGeneration { get; set; }

    [JsonPropertyName("activePublishedAtUtc")]
    public DateTime? ActivePublishedAtUtc { get; set; }

    [JsonPropertyName("hasPendingChanges")]
    public bool HasPendingChanges { get; set; }

    [JsonPropertyName("totalRegistryCount")]
    public int TotalRegistryCount { get; set; }

    [JsonPropertyName("enabledRegistryCount")]
    public int EnabledRegistryCount { get; set; }

    [JsonPropertyName("activeGenerationCount")]
    public int ActiveGenerationCount { get; set; }

    [JsonPropertyName("pendingAdditionsCount")]
    public int PendingAdditionsCount { get; set; }

    [JsonPropertyName("pendingModificationsCount")]
    public int PendingModificationsCount { get; set; }

    [JsonPropertyName("pendingDeletionsCount")]
    public int PendingDeletionsCount { get; set; }

    [JsonPropertyName("pendingChangesSummary")]
    public List<string> PendingChangesSummary { get; set; } = new();
}

public class ServerRegistryItem
{
    [JsonPropertyName("serverId")]
    public string ServerId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Custom";

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = VpnProtocol.WireGuard;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; set; } = 51820;

    [JsonPropertyName("credentialSetId")]
    public string? CredentialSetId { get; set; }

    [JsonPropertyName("inActiveGeneration")]
    public bool InActiveGeneration { get; set; }

    [JsonPropertyName("publicationStatus")]
    public string PublicationStatus { get; set; } = "NOT_PUBLISHED";

    [JsonPropertyName("activeGeneration")]
    public int ActiveGeneration { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class ProfileStoreService
{
    private readonly string _storageDirectory;
    private readonly string _registryPath;
    private readonly string _profilesStorageDir;
    private readonly IAppLogger _logger;
    private readonly CredentialSetService? _credentialSetService;
    private readonly ConcurrentDictionary<string, (ServerRegistryItem Meta, ServerProfile Profile)> _serverRegistry = new();
    private readonly object _lock = new();

    public ProfileStoreService(IConfiguration config, CredentialSetService? credentialSetService = null, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "profileservice.log");
        _credentialSetService = credentialSetService;
        var configuredPath = config["ProfileService:StoragePath"];
        _storageDirectory = !string.IsNullOrEmpty(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");

        _registryPath = Path.Combine(_storageDirectory, "server_registry.json");
        _profilesStorageDir = Path.Combine(_storageDirectory, "server_profiles");

        InitializeStorage();
    }

    public ProfileStoreService(IConfiguration config, IAppLogger? logger)
        : this(config, null, logger)
    {
    }

    public ProfileStoreService(string storageDirectory, CredentialSetService? credentialSetService = null, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "profileservice.log");
        _credentialSetService = credentialSetService;
        _storageDirectory = storageDirectory;
        _registryPath = Path.Combine(_storageDirectory, "server_registry.json");
        _profilesStorageDir = Path.Combine(_storageDirectory, "server_profiles");

        InitializeStorage();
    }

    public ProfileStoreService(string storageDirectory, IAppLogger? logger)
        : this(storageDirectory, null, logger)
    {
    }

    private void InitializeStorage()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_storageDirectory);
            Directory.CreateDirectory(Path.Combine(_storageDirectory, "generations"));
            Directory.CreateDirectory(_profilesStorageDir);

            LoadServerRegistry();

            var pointerPath = Path.Combine(_storageDirectory, "active_generation.json");
            var activeGen = GetActiveGenerationNumber();
            if (!File.Exists(pointerPath) || activeGen == 0)
            {
                var enabled = _serverRegistry.Values.Where(s => s.Meta.Enabled).ToList();
                if (enabled.Count > 0)
                {
                    _logger.Info($"Initializing active server catalog generation 1 from {enabled.Count} existing registry servers...");
                    CreateAndPublishNewGeneration("System Initialization");
                }
                else
                {
                    _logger.Info("Initializing default server catalog generation 1...");
                    CreateDefaultGeneration();
                }
            }
        }
    }

    private void LoadServerRegistry()
    {
        if (File.Exists(_registryPath))
        {
            try
            {
                var json = File.ReadAllText(_registryPath);
                var items = JsonSerializer.Deserialize<List<ServerRegistryItem>>(json);
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        // Automatic migration: default missing Protocol to WIREGUARD
                        if (string.IsNullOrWhiteSpace(item.Protocol))
                        {
                            item.Protocol = VpnProtocol.WireGuard;
                        }
                        if (string.IsNullOrWhiteSpace(item.Provider))
                        {
                            item.Provider = "Custom";
                        }
                        if (string.IsNullOrWhiteSpace(item.CountryCode))
                        {
                            item.CountryCode = item.Country;
                        }

                        var profilePath = Path.Combine(_profilesStorageDir, $"{item.ServerId}.json");
                        ServerProfile? prof = null;
                        if (File.Exists(profilePath))
                        {
                            try
                            {
                                var profJson = File.ReadAllText(profilePath);
                                prof = JsonSerializer.Deserialize<ServerProfile>(profJson);
                            }
                            catch { }
                        }

                        if (prof == null)
                        {
                            prof = new ServerProfile
                            {
                                ServerId = item.ServerId,
                                Protocol = item.Protocol,
                                Wireguard = new WireGuardProfileConfig
                                {
                                    Endpoint = item.Endpoint,
                                    Port = item.Port
                                }
                            };
                        }
                        else
                        {
                            prof.Protocol = item.Protocol;
                        }

                        _serverRegistry[item.ServerId] = (item, prof);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not load server registry: {ex.Message}");
            }
        }
    }

    private void SaveServerRegistry()
    {
        try
        {
            var items = _serverRegistry.Values.Select(v => v.Meta).ToList();
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _registryPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _registryPath, overwrite: true);

            foreach (var kvp in _serverRegistry)
            {
                var profPath = Path.Combine(_profilesStorageDir, $"{kvp.Key}.json");
                var profJson = JsonSerializer.Serialize(kvp.Value.Profile, new JsonSerializerOptions { WriteIndented = true });
                var tempProfPath = profPath + ".tmp";
                File.WriteAllText(tempProfPath, profJson);
                File.Move(tempProfPath, profPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to save server registry", ex);
        }
    }

    private void CreateDefaultGeneration()
    {
        var now = DateTime.UtcNow;
        var (privKey, pubKey) = ProfileCrypto.GenerateKeyPair();

        var defaultServers = new List<(string Id, string Name, string Country, string Region, string Endpoint, int Port)>
        {
            ("us-01", "United States (East)", "US", "us-east", "us-east.vpn.example.com", 51820),
            ("us-02", "United States (West)", "US", "us-west", "us-west.vpn.example.com", 51820),
            ("de-01", "Germany (Frankfurt)", "DE", "frankfurt", "de-01.vpn.example.com", 51820),
            ("nl-01", "Netherlands (Amsterdam)", "NL", "amsterdam", "nl-01.vpn.example.com", 51820),
            ("uk-01", "United Kingdom (London)", "UK", "london", "uk-01.vpn.example.com", 51820)
        };

        var catalog = new ServerCatalog
        {
            Schema = 1,
            Generation = 1,
            PublishedAtUtc = now,
            Servers = new List<ServerEntry>()
        };

        var manifest = new SignedManifest
        {
            Generation = 1,
            PublishedAtUtc = now
        };

        foreach (var s in defaultServers)
        {
            var entry = new ServerEntry
            {
                Id = s.Id,
                Name = s.Name,
                Country = s.Country,
                CountryCode = s.Country,
                Region = s.Region,
                Provider = "Custom",
                Protocol = VpnProtocol.WireGuard,
                Enabled = true
            };
            catalog.Servers.Add(entry);

            var profile = new ServerProfile
            {
                ServerId = s.Id,
                Protocol = VpnProtocol.WireGuard,
                Generation = 1,
                IssuedAt = now,
                ExpiresAt = now.AddDays(7),
                SchemaVersion = 1,
                Wireguard = new WireGuardProfileConfig
                {
                    Endpoint = s.Endpoint,
                    Port = s.Port,
                    Address = "10.8.0.2/32",
                    PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
                    PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA="
                }
            };

            var profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            var profileSig = ProfileCrypto.SignData(profileJson, privKey);

            manifest.ProfilePayloads[s.Id] = profileJson;
            manifest.ProfileSignatures[s.Id] = profileSig;

            var regItem = new ServerRegistryItem
            {
                ServerId = s.Id,
                Name = s.Name,
                Country = s.Country,
                CountryCode = s.Country,
                Region = s.Region,
                Provider = "Custom",
                Protocol = VpnProtocol.WireGuard,
                Enabled = true,
                Endpoint = s.Endpoint,
                Port = s.Port,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            _serverRegistry[s.Id] = (regItem, profile);
        }

        var catalogJson = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        var catalogSig = ProfileCrypto.SignData(catalogJson, privKey);

        manifest.CatalogJson = catalogJson;
        manifest.SignatureBase64 = catalogSig;

        SaveGenerationInternal(manifest, "System Initialization");
        SaveServerRegistry();
    }

    public static (bool Success, string Error, ServerEntry? Entry, ServerProfile? Profile) ParseWireGuardConfig(
        string confContent,
        string displayName,
        string country,
        string region,
        string provider = "Custom")
    {
        if (string.IsNullOrWhiteSpace(confContent))
            return (false, "WireGuard configuration content is empty.", null, null);

        var lines = confContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        string currentSection = "";
        string privateKey = "";
        string address = "";
        string dns = "";
        string peerPublicKey = "";
        string endpoint = "";
        int port = 51820;
        string allowedIps = "0.0.0.0/0, ::/0";
        int keepalive = 25;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";"))
                continue;

            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                currentSection = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                continue;
            }

            var parts = line.Split('=', 2);
            if (parts.Length != 2) continue;

            var key = parts[0].Trim().ToLowerInvariant();
            var val = parts[1].Trim();

            if (currentSection == "interface")
            {
                if (key == "privatekey") privateKey = val;
                else if (key == "address") address = string.IsNullOrEmpty(address) ? val : $"{address}, {val}";
                else if (key == "dns") dns = val;
            }
            else if (currentSection == "peer")
            {
                if (key == "publickey") peerPublicKey = val;
                else if (key == "endpoint")
                {
                    var lastColon = val.LastIndexOf(':');
                    if (lastColon > 0)
                    {
                        endpoint = val.Substring(0, lastColon).Trim();
                        if (int.TryParse(val.Substring(lastColon + 1).Trim(), out var parsedPort))
                        {
                            port = parsedPort;
                        }
                    }
                    else
                    {
                        endpoint = val;
                    }
                }
                else if (key == "allowedips") allowedIps = val;
                else if (key == "persistentkeepalive")
                {
                    if (int.TryParse(val, out var parsedKa)) keepalive = parsedKa;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(privateKey))
            return (false, "Invalid profile: Missing PrivateKey in [Interface] section.", null, null);

        if (string.IsNullOrWhiteSpace(peerPublicKey))
            return (false, "Invalid profile: Missing PublicKey in [Peer] section.", null, null);

        if (string.IsNullOrWhiteSpace(endpoint))
            return (false, "Invalid profile: Missing Endpoint in [Peer] section.", null, null);

        var serverId = "wg-" + (string.IsNullOrWhiteSpace(country) ? "srv" : country.ToLowerInvariant().Trim()) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var finalName = string.IsNullOrWhiteSpace(displayName) ? $"{country} Server ({endpoint})" : displayName.Trim();

        var entry = new ServerEntry
        {
            Id = serverId,
            Name = finalName,
            Country = country,
            CountryCode = country,
            Region = string.IsNullOrWhiteSpace(region) ? country : region.Trim(),
            Provider = string.IsNullOrWhiteSpace(provider) ? "Custom" : provider.Trim(),
            Protocol = VpnProtocol.WireGuard,
            Enabled = true
        };

        var profile = new ServerProfile
        {
            ServerId = serverId,
            Protocol = VpnProtocol.WireGuard,
            SchemaVersion = 1,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Wireguard = new WireGuardProfileConfig
            {
                Endpoint = endpoint,
                Port = port,
                Address = string.IsNullOrWhiteSpace(address) ? "10.2.0.2/32" : address,
                PrivateKey = privateKey,
                PeerPublicKey = peerPublicKey,
                AllowedIps = allowedIps,
                PersistentKeepalive = keepalive
            }
        };

        return (true, string.Empty, entry, profile);
    }

    public static (bool Success, string Error, ServerEntry? Entry, ServerProfile? Profile) ParseOpenVpnConfig(
        string ovpnContent,
        string displayName,
        string country,
        string countryCode,
        string region,
        string? city = null,
        string provider = "Custom",
        string? credentialSetId = null,
        string? username = null,
        string? password = null,
        Dictionary<string, string>? supportingFiles = null)
    {
        var validation = OpenVpnConfigParser.ParseAndValidate(ovpnContent, supportingFiles, provider);
        if (!validation.IsValid || validation.ParsedConfig == null)
        {
            return (false, validation.Error, null, null);
        }

        var config = validation.ParsedConfig;
        config.CredentialSetId = credentialSetId;
        if (!string.IsNullOrEmpty(username)) config.Username = username;
        if (!string.IsNullOrEmpty(password)) config.EncryptedPassword = DCScreenSharing.Core.Security.CredentialCrypto.Encrypt(password);

        var primaryRemote = validation.Remotes.FirstOrDefault();
        var endpointStr = primaryRemote?.Host ?? "unknown";
        var portVal = primaryRemote?.Port ?? 1194;

        var cCode = !string.IsNullOrWhiteSpace(countryCode) ? countryCode.ToLowerInvariant().Trim() : (!string.IsNullOrWhiteSpace(country) ? country.ToLowerInvariant().Trim() : "srv");
        var serverId = "ovpn-" + cCode + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        var finalName = string.IsNullOrWhiteSpace(displayName) ? $"{country} OpenVPN ({endpointStr})" : displayName.Trim();

        var entry = new ServerEntry
        {
            Id = serverId,
            Name = finalName,
            Country = string.IsNullOrWhiteSpace(country) ? countryCode : country.Trim(),
            CountryCode = !string.IsNullOrWhiteSpace(countryCode) ? countryCode.Trim() : country.Trim(),
            Region = string.IsNullOrWhiteSpace(region) ? country : region.Trim(),
            City = city,
            Provider = validation.Provider,
            Protocol = VpnProtocol.OpenVpn,
            Enabled = true
        };

        var profile = new ServerProfile
        {
            ServerId = serverId,
            Protocol = VpnProtocol.OpenVpn,
            SchemaVersion = 1,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Openvpn = config
        };

        return (true, string.Empty, entry, profile);
    }

    public IReadOnlyList<ServerRegistryItem> GetServers(string? protocol = null)
    {
        lock (_lock)
        {
            var manifest = GetCurrentManifest();
            var activeGen = GetActiveGenerationNumber();
            var activeServerIds = manifest?.ProfilePayloads != null ? new HashSet<string>(manifest.ProfilePayloads.Keys) : new HashSet<string>();
            var pubTime = manifest?.PublishedAtUtc ?? DateTime.MinValue;

            var list = new List<ServerRegistryItem>();
            foreach (var v in _serverRegistry.Values)
            {
                var meta = v.Meta;
                meta.ActiveGeneration = activeGen;
                if (!meta.Enabled)
                {
                    meta.InActiveGeneration = false;
                    meta.PublicationStatus = "DISABLED";
                }
                else if (activeServerIds.Contains(meta.ServerId))
                {
                    meta.InActiveGeneration = true;
                    if (meta.UpdatedAtUtc > pubTime.AddSeconds(1))
                    {
                        meta.PublicationStatus = "PENDING_CHANGES";
                    }
                    else
                    {
                        meta.PublicationStatus = "PUBLISHED";
                    }
                }
                else
                {
                    meta.InActiveGeneration = false;
                    meta.PublicationStatus = "NOT_PUBLISHED";
                }
                list.Add(meta);
            }

            var query = list.AsEnumerable();
            if (!string.IsNullOrEmpty(protocol))
            {
                var norm = VpnProtocol.Normalize(protocol);
                query = query.Where(s => string.Equals(s.Protocol, norm, StringComparison.OrdinalIgnoreCase));
            }

            return query.OrderBy(s => s.Name).ToList();
        }
    }

    public PublicationStatusSummary GetPublicationStatus()
    {
        lock (_lock)
        {
            var activeGen = GetActiveGenerationNumber();
            var manifest = GetCurrentManifest();
            var activeServerIds = manifest?.ProfilePayloads != null ? new HashSet<string>(manifest.ProfilePayloads.Keys) : new HashSet<string>();
            var publishedAtUtc = manifest?.PublishedAtUtc;

            var enabledServers = _serverRegistry.Values.Where(s => s.Meta.Enabled).ToList();
            var pendingAdditions = enabledServers.Where(s => !activeServerIds.Contains(s.Meta.ServerId)).ToList();
            var pendingDeletions = activeServerIds.Where(id => !_serverRegistry.TryGetValue(id, out var r) || !r.Meta.Enabled).ToList();

            var pendingModifications = new List<ServerRegistryItem>();
            if (publishedAtUtc.HasValue)
            {
                var pubTime = publishedAtUtc.Value;
                foreach (var s in enabledServers.Where(s => activeServerIds.Contains(s.Meta.ServerId)))
                {
                    if (s.Meta.UpdatedAtUtc > pubTime.AddSeconds(1))
                    {
                        pendingModifications.Add(s.Meta);
                    }
                }
            }

            var summary = new List<string>();
            foreach (var a in pendingAdditions)
            {
                summary.Add($"+ Added: {a.Meta.Name} ({a.Meta.Protocol} - {a.Meta.Provider})");
            }
            foreach (var m in pendingModifications)
            {
                summary.Add($"~ Modified: {m.Name} ({m.Protocol} - {m.Provider})");
            }
            foreach (var d in pendingDeletions)
            {
                var name = _serverRegistry.TryGetValue(d, out var item) ? item.Meta.Name : d;
                summary.Add($"- Removed/Disabled: {name}");
            }

            var hasChanges = (activeGen == 0 && enabledServers.Count > 0) ||
                             pendingAdditions.Count > 0 ||
                             pendingDeletions.Count > 0 ||
                             pendingModifications.Count > 0;

            return new PublicationStatusSummary
            {
                ActiveGeneration = activeGen,
                ActivePublishedAtUtc = publishedAtUtc,
                HasPendingChanges = hasChanges,
                TotalRegistryCount = _serverRegistry.Count,
                EnabledRegistryCount = enabledServers.Count,
                ActiveGenerationCount = activeServerIds.Count,
                PendingAdditionsCount = pendingAdditions.Count,
                PendingModificationsCount = pendingModifications.Count,
                PendingDeletionsCount = pendingDeletions.Count,
                PendingChangesSummary = summary
            };
        }
    }

    public ServerRegistryItem? GetServerById(string serverId)
    {
        if (_serverRegistry.TryGetValue(serverId, out var item))
        {
            return item.Meta;
        }
        return null;
    }

    public bool AddWireGuardServer(ServerEntry entry, ServerProfile profile, string country = "", string provider = "Custom")
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var regItem = new ServerRegistryItem
            {
                ServerId = entry.Id,
                Name = entry.Name,
                Country = string.IsNullOrWhiteSpace(country) ? entry.Region : country,
                CountryCode = !string.IsNullOrWhiteSpace(entry.CountryCode) ? entry.CountryCode : country,
                Region = entry.Region,
                City = entry.City,
                Provider = string.IsNullOrWhiteSpace(provider) ? entry.Provider : provider,
                Protocol = VpnProtocol.WireGuard,
                Enabled = entry.Enabled,
                Endpoint = profile.Wireguard.Endpoint,
                Port = profile.Wireguard.Port,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            entry.Protocol = VpnProtocol.WireGuard;
            profile.Protocol = VpnProtocol.WireGuard;

            _serverRegistry[entry.Id] = (regItem, profile);
            SaveServerRegistry();
            return true;
        }
    }

    public bool AddOpenVpnServer(ServerEntry entry, ServerProfile profile, string country = "", string countryCode = "", string? city = null, string provider = "Custom", string? credentialSetId = null)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var primaryRemote = profile.Openvpn?.RemoteEndpoints.FirstOrDefault();

            var regItem = new ServerRegistryItem
            {
                ServerId = entry.Id,
                Name = entry.Name,
                Country = string.IsNullOrWhiteSpace(country) ? entry.Region : country,
                CountryCode = !string.IsNullOrWhiteSpace(countryCode) ? countryCode : entry.CountryCode,
                Region = entry.Region,
                City = city ?? entry.City,
                Provider = string.IsNullOrWhiteSpace(provider) ? entry.Provider : provider,
                Protocol = VpnProtocol.OpenVpn,
                Enabled = entry.Enabled,
                Endpoint = primaryRemote?.Host ?? string.Empty,
                Port = primaryRemote?.Port ?? 1194,
                CredentialSetId = credentialSetId ?? profile.Openvpn?.CredentialSetId,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            entry.Protocol = VpnProtocol.OpenVpn;
            profile.Protocol = VpnProtocol.OpenVpn;

            _serverRegistry[entry.Id] = (regItem, profile);
            SaveServerRegistry();
            return true;
        }
    }

    public bool UpdateServer(string serverId, string displayName, string region, bool enabled)
    {
        lock (_lock)
        {
            if (_serverRegistry.TryGetValue(serverId, out var existing))
            {
                var meta = existing.Meta;
                meta.Name = string.IsNullOrWhiteSpace(displayName) ? meta.Name : displayName.Trim();
                meta.Region = string.IsNullOrWhiteSpace(region) ? meta.Region : region.Trim();
                meta.Enabled = enabled;
                meta.UpdatedAtUtc = DateTime.UtcNow;

                _serverRegistry[serverId] = (meta, existing.Profile);
                SaveServerRegistry();
                return true;
            }
            return false;
        }
    }

    public bool UpdateOpenVpnServer(string serverId, string displayName, string region, string? city, string provider, string? credentialSetId, bool enabled)
    {
        lock (_lock)
        {
            if (_serverRegistry.TryGetValue(serverId, out var existing))
            {
                var meta = existing.Meta;
                meta.Name = string.IsNullOrWhiteSpace(displayName) ? meta.Name : displayName.Trim();
                meta.Region = string.IsNullOrWhiteSpace(region) ? meta.Region : region.Trim();
                meta.City = city;
                if (!string.IsNullOrWhiteSpace(provider)) meta.Provider = provider.Trim();
                meta.CredentialSetId = credentialSetId;
                meta.Enabled = enabled;
                meta.UpdatedAtUtc = DateTime.UtcNow;

                var prof = existing.Profile;
                if (prof.Openvpn != null)
                {
                    prof.Openvpn.CredentialSetId = credentialSetId;
                }

                _serverRegistry[serverId] = (meta, prof);
                SaveServerRegistry();
                return true;
            }
            return false;
        }
    }

    public bool SetServerEnabled(string serverId, bool enabled)
    {
        lock (_lock)
        {
            if (_serverRegistry.TryGetValue(serverId, out var existing))
            {
                existing.Meta.Enabled = enabled;
                existing.Meta.UpdatedAtUtc = DateTime.UtcNow;
                _serverRegistry[serverId] = existing;
                SaveServerRegistry();
                return true;
            }
            return false;
        }
    }

    public bool DeleteServer(string serverId)
    {
        lock (_lock)
        {
            if (_serverRegistry.TryRemove(serverId, out _))
            {
                var profPath = Path.Combine(_profilesStorageDir, $"{serverId}.json");
                if (File.Exists(profPath))
                {
                    try { File.Delete(profPath); } catch { }
                }
                SaveServerRegistry();
                return true;
            }
            return false;
        }
    }

    public (bool Success, string Error, int NewGeneration) CreateAndPublishNewGeneration(string publishedBy, string? privateKeyPem = null)
    {
        lock (_lock)
        {
            try
            {
                var enabledServers = _serverRegistry.Values.Where(s => s.Meta.Enabled).ToList();
                if (enabledServers.Count == 0)
                {
                    return (false, "Cannot create generation with 0 enabled servers.", 0);
                }

                // Pre-publication validation
                foreach (var s in enabledServers)
                {
                    if (VpnProtocol.IsOpenVpn(s.Meta.Protocol))
                    {
                        if (s.Profile.Openvpn == null || s.Profile.Openvpn.RemoteEndpoints.Count == 0)
                        {
                            return (false, $"Validation failed: OpenVPN server '{s.Meta.Name}' has no remote endpoints configured.", 0);
                        }

                        var credId = !string.IsNullOrEmpty(s.Profile.Openvpn.CredentialSetId)
                            ? s.Profile.Openvpn.CredentialSetId
                            : s.Meta.CredentialSetId;

                        if (!string.IsNullOrEmpty(credId) && _credentialSetService != null)
                        {
                            var cred = _credentialSetService.GetById(credId);
                            if (cred == null)
                            {
                                return (false, $"Validation failed: OpenVPN server '{s.Meta.Name}' is linked to missing Credential Set '{credId}'.", 0);
                            }
                        }
                    }
                    else if (VpnProtocol.IsWireGuard(s.Meta.Protocol))
                    {
                        if (s.Profile.Wireguard == null || string.IsNullOrWhiteSpace(s.Profile.Wireguard.Endpoint))
                        {
                            return (false, $"Validation failed: WireGuard server '{s.Meta.Name}' has no endpoint configured.", 0);
                        }
                    }
                }

                var currentGen = GetActiveGenerationNumber();
                var nextGen = currentGen + 1;
                var now = DateTime.UtcNow;

                var (privKey, pubKey) = ProfileCrypto.GenerateKeyPair();
                if (!string.IsNullOrEmpty(privateKeyPem))
                {
                    privKey = privateKeyPem;
                }

                var catalog = new ServerCatalog
                {
                    Schema = 1,
                    Generation = nextGen,
                    PublishedAtUtc = now,
                    Servers = enabledServers.Select(s => new ServerEntry
                    {
                        Id = s.Meta.ServerId,
                        Name = s.Meta.Name,
                        Country = s.Meta.Country,
                        CountryCode = s.Meta.CountryCode,
                        Region = s.Meta.Region,
                        City = s.Meta.City,
                        Provider = s.Meta.Provider,
                        Protocol = s.Meta.Protocol,
                        Enabled = true
                    }).ToList()
                };

                var catalogJson = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
                var catalogSig = ProfileCrypto.SignData(catalogJson, privKey);

                var manifest = new SignedManifest
                {
                    CatalogJson = catalogJson,
                    SignatureBase64 = catalogSig,
                    Generation = nextGen,
                    PublishedAtUtc = now
                };

                foreach (var s in enabledServers)
                {
                    var prof = s.Profile;
                    prof.Generation = nextGen;
                    prof.IssuedAt = now;
                    prof.ExpiresAt = now.AddDays(30);
                    prof.Protocol = s.Meta.Protocol;

                    // If OpenVPN server is linked to a CredentialSetId, resolve latest credentials
                    if (VpnProtocol.IsOpenVpn(prof.Protocol) && prof.Openvpn != null)
                    {
                        var credId = !string.IsNullOrEmpty(prof.Openvpn.CredentialSetId)
                            ? prof.Openvpn.CredentialSetId
                            : s.Meta.CredentialSetId;

                        if (!string.IsNullOrEmpty(credId) && _credentialSetService != null)
                        {
                            var (uname, decPass) = _credentialSetService.ResolveCredentials(credId);
                            if (uname != null) prof.Openvpn.Username = uname;
                            if (decPass != null)
                            {
                                prof.Openvpn.EncryptedPassword = DCScreenSharing.Core.Security.CredentialCrypto.Encrypt(decPass);
                            }
                        }
                    }

                    var profileJson = JsonSerializer.Serialize(prof, new JsonSerializerOptions { WriteIndented = true });
                    var profileSig = ProfileCrypto.SignData(profileJson, privKey);

                    manifest.ProfilePayloads[s.Meta.ServerId] = profileJson;
                    manifest.ProfileSignatures[s.Meta.ServerId] = profileSig;
                }

                SaveGenerationInternal(manifest, publishedBy);
                _logger.Info($"Published new generation {nextGen} by {publishedBy} with {enabledServers.Count} servers (WG: {enabledServers.Count(s => VpnProtocol.IsWireGuard(s.Meta.Protocol))}, OVPN: {enabledServers.Count(s => VpnProtocol.IsOpenVpn(s.Meta.Protocol))}).");
                return (true, string.Empty, nextGen);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to create new generation", ex);
                return (false, ex.Message, 0);
            }
        }
    }

    public SignedManifest? GetCurrentManifest()
    {
        lock (_lock)
        {
            var activeGen = GetActiveGenerationNumber();
            if (activeGen <= 0) return null;

            var manifestPath = Path.Combine(_storageDirectory, "generations", $"manifest_gen_{activeGen}.json");
            if (!File.Exists(manifestPath)) return null;

            var json = File.ReadAllText(manifestPath);
            return JsonSerializer.Deserialize<SignedManifest>(json);
        }
    }

    public ServerCatalog? GetCurrentCatalog()
    {
        var manifest = GetCurrentManifest();
        if (manifest == null || string.IsNullOrEmpty(manifest.CatalogJson)) return null;

        return JsonSerializer.Deserialize<ServerCatalog>(manifest.CatalogJson);
    }

    public ServerProfile? GetServerProfile(string serverId)
    {
        var manifest = GetCurrentManifest();
        if (manifest != null && manifest.ProfilePayloads.TryGetValue(serverId, out var profileJson))
        {
            return JsonSerializer.Deserialize<ServerProfile>(profileJson);
        }

        // Fallback to internal registry if active generation doesn't have it yet
        if (_serverRegistry.TryGetValue(serverId, out var reg))
        {
            return reg.Profile;
        }

        return null;
    }

    public bool PublishGeneration(SignedManifest manifest, string publishedBy)
    {
        lock (_lock)
        {
            try
            {
                if (manifest == null || string.IsNullOrEmpty(manifest.CatalogJson) || string.IsNullOrEmpty(manifest.SignatureBase64))
                {
                    _logger.Warning("Rejected publication: Missing manifest catalog or signature.");
                    return false;
                }

                var catalog = JsonSerializer.Deserialize<ServerCatalog>(manifest.CatalogJson);
                if (catalog == null || catalog.Servers.Count == 0)
                {
                    _logger.Warning("Rejected publication: Catalog is empty or invalid.");
                    return false;
                }

                var currentGen = GetActiveGenerationNumber();
                if (manifest.Generation <= currentGen)
                {
                    _logger.Warning($"Rejected publication: Generation {manifest.Generation} must be greater than current active generation {currentGen}.");
                    return false;
                }

                SaveGenerationInternal(manifest, publishedBy);
                _logger.Info($"Successfully published generation {manifest.Generation} by {publishedBy}.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to publish generation", ex);
                return false;
            }
        }
    }

    public bool RollbackToGeneration(int targetGeneration)
    {
        lock (_lock)
        {
            try
            {
                var manifestPath = Path.Combine(_storageDirectory, "generations", $"manifest_gen_{targetGeneration}.json");
                if (!File.Exists(manifestPath))
                {
                    _logger.Warning($"Rollback failed: Generation {targetGeneration} does not exist in storage.");
                    return false;
                }

                SetActiveGenerationNumber(targetGeneration);
                _logger.Info($"Successfully rolled back to generation {targetGeneration}.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed rollback to generation {targetGeneration}", ex);
                return false;
            }
        }
    }

    public IReadOnlyList<GenerationRecord> GetGenerationHistory()
    {
        lock (_lock)
        {
            var history = new List<GenerationRecord>();
            var activeGen = GetActiveGenerationNumber();
            var genFiles = Directory.GetFiles(Path.Combine(_storageDirectory, "generations"), "manifest_gen_*.json");

            foreach (var file in genFiles)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var manifest = JsonSerializer.Deserialize<SignedManifest>(json);
                    if (manifest != null)
                    {
                        var catalog = JsonSerializer.Deserialize<ServerCatalog>(manifest.CatalogJson);
                        var srvs = catalog?.Servers ?? new List<ServerEntry>();
                        history.Add(new GenerationRecord
                        {
                            Generation = manifest.Generation,
                            PublishedAtUtc = manifest.PublishedAtUtc,
                            ServerCount = srvs.Count,
                            WireGuardCount = srvs.Count(s => VpnProtocol.IsWireGuard(s.Protocol)),
                            OpenVpnCount = srvs.Count(s => VpnProtocol.IsOpenVpn(s.Protocol)),
                            IsActive = manifest.Generation == activeGen
                        });
                    }
                }
                catch { }
            }

            return history.OrderByDescending(h => h.Generation).ToList();
        }
    }

    private void SaveGenerationInternal(SignedManifest manifest, string publishedBy)
    {
        var genDir = Path.Combine(_storageDirectory, "generations");
        Directory.CreateDirectory(genDir);
        var manifestPath = Path.Combine(genDir, $"manifest_gen_{manifest.Generation}.json");
        var tempPath = Path.Combine(genDir, $"manifest_gen_{manifest.Generation}.tmp");

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, manifestPath, overwrite: true);

        SetActiveGenerationNumber(manifest.Generation);
    }

    public int GetActiveGenerationNumber()
    {
        var pointerPath = Path.Combine(_storageDirectory, "active_generation.json");
        if (!File.Exists(pointerPath)) return 0;

        try
        {
            var json = File.ReadAllText(pointerPath);
            var pointer = JsonSerializer.Deserialize<ActiveGenerationPointer>(json);
            return pointer?.ActiveGeneration ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private void SetActiveGenerationNumber(int genNumber)
    {
        var pointerPath = Path.Combine(_storageDirectory, "active_generation.json");
        var tempPointerPath = Path.Combine(_storageDirectory, "active_generation.tmp");

        var pointer = new ActiveGenerationPointer
        {
            ActiveGeneration = genNumber,
            UpdatedAtUtc = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(pointer, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPointerPath, json);
        File.Move(tempPointerPath, pointerPath, overwrite: true);
    }
}
