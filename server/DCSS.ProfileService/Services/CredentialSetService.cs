using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCScreenSharing.Core.Security;
using DCScreenSharing.Shared.Logging;

namespace DCSS.ProfileService.Services;

public class CredentialSetRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Custom";

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("encryptedPassword")]
    public string EncryptedPassword { get; set; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class CredentialSetDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "Custom";

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTime UpdatedAtUtc { get; set; }
}

public class CredentialSetService
{
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, CredentialSetRecord> _sets = new();
    private readonly IAppLogger _logger;
    private readonly object _lock = new();

    public CredentialSetService(IConfiguration config, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "credential_sets.log");
        var basePath = config["ProfileService:StoragePath"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
        Directory.CreateDirectory(basePath);
        _storagePath = Path.Combine(basePath, "credential_sets.json");

        LoadState();
    }

    public CredentialSetService(string storagePath, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath(), "credential_sets.log");
        _storagePath = storagePath;
        var dir = Path.GetDirectoryName(storagePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        LoadState();
    }

    private void LoadState()
    {
        lock (_lock)
        {
            if (File.Exists(_storagePath))
            {
                try
                {
                    var json = File.ReadAllText(_storagePath);
                    var list = JsonSerializer.Deserialize<List<CredentialSetRecord>>(json);
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            _sets[item.Id] = item;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Could not load credential sets: {ex.Message}");
                }
            }
        }
    }

    private void SaveState()
    {
        lock (_lock)
        {
            try
            {
                var list = _sets.Values.ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                var temp = _storagePath + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _storagePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to save credential sets", ex);
            }
        }
    }

    public IReadOnlyList<CredentialSetDto> GetAllDtos()
    {
        return _sets.Values.OrderBy(s => s.Name).Select(ToDto).ToList();
    }

    public CredentialSetRecord? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        _sets.TryGetValue(id, out var record);
        return record;
    }

    public (string? Username, string? DecryptedPassword) ResolveCredentials(string credentialSetId)
    {
        var record = GetById(credentialSetId);
        if (record == null) return (null, null);

        var plainPass = !string.IsNullOrEmpty(record.EncryptedPassword)
            ? CredentialCrypto.Decrypt(record.EncryptedPassword)
            : null;

        return (record.Username, plainPass);
    }

    public CredentialSetDto Create(string name, string provider, string username, string plaintextPassword)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var encrypted = !string.IsNullOrEmpty(plaintextPassword)
                ? CredentialCrypto.Encrypt(plaintextPassword)
                : string.Empty;

            var record = new CredentialSetRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "Credential Set" : name.Trim(),
                Provider = string.IsNullOrWhiteSpace(provider) ? "Custom" : provider.Trim(),
                Username = username?.Trim() ?? string.Empty,
                EncryptedPassword = encrypted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            _sets[record.Id] = record;
            SaveState();
            _logger.Info($"Created credential set '{record.Name}' (ID: {record.Id}) for provider '{record.Provider}'.");
            return ToDto(record);
        }
    }

    public bool Update(string id, string? name, string? provider, string? username, string? newPlaintextPassword)
    {
        lock (_lock)
        {
            if (!_sets.TryGetValue(id, out var record))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(name)) record.Name = name.Trim();
            if (!string.IsNullOrWhiteSpace(provider)) record.Provider = provider.Trim();
            if (username != null) record.Username = username.Trim();

            if (!string.IsNullOrEmpty(newPlaintextPassword))
            {
                record.EncryptedPassword = CredentialCrypto.Encrypt(newPlaintextPassword);
            }

            record.UpdatedAtUtc = DateTime.UtcNow;
            _sets[id] = record;
            SaveState();
            _logger.Info($"Updated credential set '{record.Name}' (ID: {id}).");
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            if (_sets.TryRemove(id, out var removed))
            {
                SaveState();
                _logger.Info($"Deleted credential set '{removed.Name}' (ID: {id}).");
                return true;
            }
            return false;
        }
    }

    private static CredentialSetDto ToDto(CredentialSetRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Provider = record.Provider,
        Username = record.Username,
        HasPassword = !string.IsNullOrEmpty(record.EncryptedPassword),
        CreatedAtUtc = record.CreatedAtUtc,
        UpdatedAtUtc = record.UpdatedAtUtc
    };
}
