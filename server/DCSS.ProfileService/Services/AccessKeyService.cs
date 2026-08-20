using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DCSS.ProfileService.Services;

public static class AccessKeyType
{
    public const string SingleUse = "SINGLE_USE";
    public const string Group = "GROUP";
}

public static class AccessKeyStatus
{
    public const string Active = "Active";
    public const string Disabled = "Disabled";
    public const string Revoked = "Revoked";
    public const string Expired = "Expired";
    public const string Consumed = "Consumed";
    public const string CapacityReached = "Capacity Reached";
}

public class AccessKeyRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = AccessKeyType.SingleUse;
    public string CodeHash { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAtUtc { get; set; }
    public int? MaxUses { get; set; } = 1;
    public int UseCount { get; set; } = 0;
    public string Status { get; set; } = AccessKeyStatus.Active;
    public DateTime? LastUsedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "Admin";
}

public class AccessKeyService
{
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, AccessKeyRecord> _keysById = new();
    private readonly ConcurrentDictionary<string, string> _hashToIdMap = new();
    private readonly object _lock = new();
    private const string KeyAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public AccessKeyService(IConfiguration config)
    {
        var basePath = config["ProfileService:StoragePath"] ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage");
        Directory.CreateDirectory(basePath);
        _storagePath = Path.Combine(basePath, "access_keys.json");

        LoadState();
    }

    public AccessKeyService(string storagePath)
    {
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
                    var list = JsonSerializer.Deserialize<List<AccessKeyRecord>>(json);
                    if (list != null)
                    {
                        foreach (var key in list)
                        {
                            // Update status if expired while offline
                            if (key.Status == AccessKeyStatus.Active && key.ExpiresAtUtc.HasValue && key.ExpiresAtUtc.Value < DateTime.UtcNow)
                            {
                                key.Status = AccessKeyStatus.Expired;
                            }
                            _keysById[key.Id] = key;
                            _hashToIdMap[key.CodeHash] = key.Id;
                        }
                    }
                }
                catch { }
            }
        }
    }

    private void SaveState()
    {
        lock (_lock)
        {
            try
            {
                var list = _keysById.Values.ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                var tempPath = _storagePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _storagePath, overwrite: true);
            }
            catch { }
        }
    }

    public static string HashCode(string plaintextCode)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(plaintextCode.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateCryptographicCode()
    {
        var result = new StringBuilder("DCSS-", 24);
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);

        for (int i = 0; i < 16; i++)
        {
            int idx = bytes[i] % KeyAlphabet.Length;
            result.Append(KeyAlphabet[idx]);
            if (i == 3 || i == 7 || i == 11)
            {
                result.Append('-');
            }
        }

        return result.ToString();
    }

    public (string PlaintextCode, AccessKeyRecord Record) CreateAccessKey(
        string name,
        string type,
        DateTime? expiresAtUtc,
        int? maxUses,
        string createdBy = "Admin")
    {
        lock (_lock)
        {
            var isSingleUse = string.Equals(type, AccessKeyType.SingleUse, StringComparison.OrdinalIgnoreCase);
            var actualType = isSingleUse ? AccessKeyType.SingleUse : AccessKeyType.Group;
            var actualMaxUses = isSingleUse ? 1 : (maxUses is > 0 ? maxUses : null);

            var plaintextCode = GenerateCryptographicCode();
            var hash = HashCode(plaintextCode);

            var record = new AccessKeyRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? (isSingleUse ? "Single-Use Key" : "Group Key") : name.Trim(),
                Type = actualType,
                CodeHash = hash,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = expiresAtUtc,
                MaxUses = actualMaxUses,
                UseCount = 0,
                Status = AccessKeyStatus.Active,
                CreatedBy = createdBy
            };

            _keysById[record.Id] = record;
            _hashToIdMap[hash] = record.Id;
            SaveState();

            return (plaintextCode, record);
        }
    }

    public (bool Success, string Error, AccessKeyRecord? Key) ValidateAndConsumeKey(string plaintextOrHash)
    {
        if (string.IsNullOrWhiteSpace(plaintextOrHash))
        {
            return (false, "Access key code is required.", null);
        }

        lock (_lock)
        {
            var hash = HashCode(plaintextOrHash);
            if (!_hashToIdMap.TryGetValue(hash, out var keyId) || !_keysById.TryGetValue(keyId, out var key))
            {
                // Also check if raw string was already a hash
                var directHash = plaintextOrHash.Trim().ToLowerInvariant();
                if (_hashToIdMap.TryGetValue(directHash, out keyId) && _keysById.TryGetValue(keyId, out key))
                {
                    // Matched by direct hash
                }
                else
                {
                    return (false, "Invalid access key.", null);
                }
            }

            if (key.Status == AccessKeyStatus.Revoked)
            {
                return (false, "Access key has been revoked by an administrator.", key);
            }

            if (key.Status == AccessKeyStatus.Disabled)
            {
                return (false, "Access key is currently disabled.", key);
            }

            if (key.ExpiresAtUtc.HasValue && key.ExpiresAtUtc.Value < DateTime.UtcNow)
            {
                key.Status = AccessKeyStatus.Expired;
                SaveState();
                return (false, "Access key has expired.", key);
            }

            if (key.MaxUses.HasValue && key.UseCount >= key.MaxUses.Value)
            {
                key.Status = key.MaxUses.Value == 1 ? AccessKeyStatus.Consumed : AccessKeyStatus.CapacityReached;
                SaveState();
                return (false, "Access key capacity reached / consumed.", key);
            }

            // Atomic Increment
            key.UseCount++;
            key.LastUsedAtUtc = DateTime.UtcNow;

            if (key.MaxUses.HasValue && key.UseCount >= key.MaxUses.Value)
            {
                key.Status = key.MaxUses.Value == 1 ? AccessKeyStatus.Consumed : AccessKeyStatus.CapacityReached;
            }

            SaveState();
            return (true, string.Empty, key);
        }
    }

    public AccessKeyRecord? GetKeyById(string id)
    {
        _keysById.TryGetValue(id, out var key);
        return key;
    }

    public AccessKeyRecord? GetKeyByHash(string hash)
    {
        if (_hashToIdMap.TryGetValue(hash.ToLowerInvariant(), out var keyId) && _keysById.TryGetValue(keyId, out var key))
        {
            return key;
        }
        return null;
    }

    public IReadOnlyList<AccessKeyRecord> GetAllKeys()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var key in _keysById.Values)
            {
                if (key.Status == AccessKeyStatus.Active && key.ExpiresAtUtc.HasValue && key.ExpiresAtUtc.Value < now)
                {
                    key.Status = AccessKeyStatus.Expired;
                }
            }
            return _keysById.Values.OrderByDescending(k => k.CreatedAtUtc).ToList();
        }
    }

    public bool DisableKey(string id)
    {
        lock (_lock)
        {
            if (_keysById.TryGetValue(id, out var key))
            {
                key.Status = AccessKeyStatus.Disabled;
                SaveState();
                return true;
            }
            return false;
        }
    }

    public bool EnableKey(string id)
    {
        lock (_lock)
        {
            if (_keysById.TryGetValue(id, out var key))
            {
                if (key.ExpiresAtUtc.HasValue && key.ExpiresAtUtc.Value < DateTime.UtcNow)
                {
                    key.Status = AccessKeyStatus.Expired;
                }
                else if (key.MaxUses.HasValue && key.UseCount >= key.MaxUses.Value)
                {
                    key.Status = key.MaxUses.Value == 1 ? AccessKeyStatus.Consumed : AccessKeyStatus.CapacityReached;
                }
                else
                {
                    key.Status = AccessKeyStatus.Active;
                }
                SaveState();
                return true;
            }
            return false;
        }
    }

    public bool RevokeKey(string id)
    {
        lock (_lock)
        {
            if (_keysById.TryGetValue(id, out var key))
            {
                key.Status = AccessKeyStatus.Revoked;
                SaveState();
                return true;
            }
            return false;
        }
    }
}
