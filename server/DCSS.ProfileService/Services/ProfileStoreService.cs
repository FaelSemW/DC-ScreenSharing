using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Shared.Logging;

namespace DCSS.ProfileService.Services;

public class GenerationRecord
{
    public int Generation { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public string PublishedBy { get; set; } = "Maintainer";
    public int ServerCount { get; set; }
    public bool IsActive { get; set; }
}

public class ActiveGenerationPointer
{
    public int ActiveGeneration { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class ServerRegistryItem
{
    public string ServerId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Endpoint { get; set; } = string.Empty;
    public int Port { get; set; } = 51820;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class ProfileStoreService
{
    private readonly string _storageDirectory;
    private readonly string _registryPath;
    private readonly string _profilesStorageDir;
    private readonly IAppLogger _logger;
    private readonly ConcurrentDictionary<string, (ServerRegistryItem Meta, ServerProfile Profile)> _serverRegistry = new();
    private readonly object _lock = new();

    public ProfileStoreService(IConfiguration config, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "profileservice.log");
        var configuredPath = config["ProfileService:StoragePath"];
        _storageDirectory = !string.IsNullOrEmpty(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");

        _registryPath = Path.Combine(_storageDirectory, "server_registry.json");
        _profilesStorageDir = Path.Combine(_storageDirectory, "server_profiles");

        InitializeStorage();
    }

    public ProfileStoreService(string storageDirectory, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "profileservice.log");
        _storageDirectory = storageDirectory;
        _registryPath = Path.Combine(_storageDirectory, "server_registry.json");
        _profilesStorageDir = Path.Combine(_storageDirectory, "server_profiles");

        InitializeStorage();
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
            if (!File.Exists(pointerPath))
            {
                _logger.Info("Initializing default server catalog generation 1...");
                CreateDefaultGeneration();
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
                                Wireguard = new WireGuardProfileConfig
                                {
                                    Endpoint = item.Endpoint,
                                    Port = item.Port
                                }
                            };
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
            var entry = new ServerEntry { Id = s.Id, Name = s.Name, Region = s.Region, Enabled = true };
            catalog.Servers.Add(entry);

            var profile = new ServerProfile
            {
                ServerId = s.Id,
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
                Region = s.Region,
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
        string region)
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
            Region = string.IsNullOrWhiteSpace(region) ? country : region.Trim(),
            Enabled = true
        };

        var profile = new ServerProfile
        {
            ServerId = serverId,
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

    public IReadOnlyList<ServerRegistryItem> GetServers()
    {
        return _serverRegistry.Values.Select(v => v.Meta).OrderBy(s => s.Name).ToList();
    }

    public ServerRegistryItem? GetServerById(string serverId)
    {
        if (_serverRegistry.TryGetValue(serverId, out var item))
        {
            return item.Meta;
        }
        return null;
    }

    public bool AddServer(ServerEntry entry, ServerProfile profile, string country = "")
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var regItem = new ServerRegistryItem
            {
                ServerId = entry.Id,
                Name = entry.Name,
                Country = string.IsNullOrWhiteSpace(country) ? entry.Region : country,
                Region = entry.Region,
                Enabled = entry.Enabled,
                Endpoint = profile.Wireguard.Endpoint,
                Port = profile.Wireguard.Port,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

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
                        Region = s.Meta.Region,
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

                    var profileJson = JsonSerializer.Serialize(prof, new JsonSerializerOptions { WriteIndented = true });
                    var profileSig = ProfileCrypto.SignData(profileJson, privKey);

                    manifest.ProfilePayloads[s.Meta.ServerId] = profileJson;
                    manifest.ProfileSignatures[s.Meta.ServerId] = profileSig;
                }

                SaveGenerationInternal(manifest, publishedBy);
                _logger.Info($"Published new generation {nextGen} by {publishedBy} with {enabledServers.Count} servers.");
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
                        history.Add(new GenerationRecord
                        {
                            Generation = manifest.Generation,
                            PublishedAtUtc = manifest.PublishedAtUtc,
                            ServerCount = catalog?.Servers.Count ?? 0,
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
