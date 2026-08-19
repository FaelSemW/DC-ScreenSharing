using System.Text.Json;
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

public class ProfileStoreService
{
    private readonly string _storageDirectory;
    private readonly IAppLogger _logger;
    private readonly object _lock = new();

    public ProfileStoreService(IConfiguration config, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "profileservice.log");
        var configuredPath = config["ProfileService:StoragePath"];
        _storageDirectory = !string.IsNullOrEmpty(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");

        InitializeStorage();
    }

    public ProfileStoreService(string storageDirectory, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "profileservice.log");
        _storageDirectory = storageDirectory;
        InitializeStorage();
    }

    private void InitializeStorage()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_storageDirectory);
            Directory.CreateDirectory(Path.Combine(_storageDirectory, "generations"));

            var pointerPath = Path.Combine(_storageDirectory, "active_generation.json");
            if (!File.Exists(pointerPath))
            {
                _logger.Info("Initializing default server catalog generation 1...");
                CreateDefaultGeneration();
            }
        }
    }

    private void CreateDefaultGeneration()
    {
        var now = DateTime.UtcNow;
        var (privKey, pubKey) = ProfileCrypto.GenerateKeyPair();

        var catalog = new ServerCatalog
        {
            Schema = 1,
            Generation = 1,
            PublishedAtUtc = now,
            Servers = new List<ServerEntry>
            {
                new() { Id = "us-01", Name = "United States (East)", Region = "US", Enabled = true },
                new() { Id = "us-02", Name = "United States (West)", Region = "US", Enabled = true },
                new() { Id = "de-01", Name = "Germany (Frankfurt)", Region = "DE", Enabled = true },
                new() { Id = "nl-01", Name = "Netherlands (Amsterdam)", Region = "NL", Enabled = true },
                new() { Id = "uk-01", Name = "United Kingdom (London)", Region = "UK", Enabled = true }
            }
        };

        var catalogJson = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        var catalogSig = ProfileCrypto.SignData(catalogJson, privKey);

        var manifest = new SignedManifest
        {
            CatalogJson = catalogJson,
            SignatureBase64 = catalogSig,
            Generation = 1,
            PublishedAtUtc = now
        };

        foreach (var s in catalog.Servers)
        {
            var profile = new ServerProfile
            {
                ServerId = s.Id,
                Generation = 1,
                IssuedAt = now,
                ExpiresAt = now.AddDays(7),
                SchemaVersion = 1,
                Wireguard = new WireGuardProfileConfig
                {
                    Endpoint = $"{s.Id}.vpn.example.com",
                    Port = 51820,
                    Address = "10.8.0.2/32",
                    PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
                    PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA="
                }
            };

            var profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            var profileSig = ProfileCrypto.SignData(profileJson, privKey);

            manifest.ProfilePayloads[s.Id] = profileJson;
            manifest.ProfileSignatures[s.Id] = profileSig;
        }

        SaveGenerationInternal(manifest, "System Initialization");
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
        if (manifest == null) return null;

        if (manifest.ProfilePayloads.TryGetValue(serverId, out var profileJson))
        {
            return JsonSerializer.Deserialize<ServerProfile>(profileJson);
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
