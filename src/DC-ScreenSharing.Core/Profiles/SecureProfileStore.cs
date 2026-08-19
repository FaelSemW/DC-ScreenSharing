using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.Profiles;

public class SecureProfileStore
{
    private readonly string _storageDirectory;
    private readonly IAppLogger _logger;
    private readonly object _fileLock = new();

    public SecureProfileStore(string? baseDir = null, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath());
        var root = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing");
        _storageDirectory = Path.Combine(root, "profiles");

        try
        {
            Directory.CreateDirectory(_storageDirectory);
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to create profile store directory at {_storageDirectory}", ex);
        }
    }

    public bool SaveProfile(ServerProfile profile)
    {
        lock (_fileLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
                var plainBytes = Encoding.UTF8.GetBytes(json);
                var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

                var targetPath = Path.Combine(_storageDirectory, $"{profile.ServerId}.dat");
                var tempPath = Path.Combine(_storageDirectory, $"{profile.ServerId}.tmp");
                var backupPath = Path.Combine(_storageDirectory, $"{profile.ServerId}.prev");

                File.WriteAllBytes(tempPath, encryptedBytes);

                if (File.Exists(targetPath))
                {
                    File.Copy(targetPath, backupPath, overwrite: true);
                }

                File.Move(tempPath, targetPath, overwrite: true);
                _logger.Info($"Securely saved profile for server '{profile.ServerId}' (Gen {profile.Generation}).");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to securely save profile for server '{profile.ServerId}'", ex);
                return false;
            }
        }
    }

    public ServerProfile? LoadProfile(string serverId)
    {
        lock (_fileLock)
        {
            try
            {
                var targetPath = Path.Combine(_storageDirectory, $"{serverId}.dat");
                if (!File.Exists(targetPath))
                    return null;

                var encryptedBytes = File.ReadAllBytes(targetPath);
                var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plainBytes);

                return JsonSerializer.Deserialize<ServerProfile>(json);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load/decrypt profile for server '{serverId}'. Attempting rollback.", ex);
                return RollbackProfile(serverId);
            }
        }
    }

    public ServerProfile? RollbackProfile(string serverId)
    {
        lock (_fileLock)
        {
            try
            {
                var backupPath = Path.Combine(_storageDirectory, $"{serverId}.prev");
                if (!File.Exists(backupPath))
                    return null;

                var encryptedBytes = File.ReadAllBytes(backupPath);
                var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(plainBytes);

                var profile = JsonSerializer.Deserialize<ServerProfile>(json);
                if (profile != null)
                {
                    var targetPath = Path.Combine(_storageDirectory, $"{serverId}.dat");
                    File.Copy(backupPath, targetPath, overwrite: true);
                    _logger.Info($"Successfully rolled back server '{serverId}' to generation {profile.Generation}.");
                }
                return profile;
            }
            catch (Exception ex)
            {
                _logger.Error($"Rollback failed for server '{serverId}'", ex);
                return null;
            }
        }
    }

    public void DeleteProfile(string serverId)
    {
        lock (_fileLock)
        {
            try
            {
                var targetPath = Path.Combine(_storageDirectory, $"{serverId}.dat");
                var backupPath = Path.Combine(_storageDirectory, $"{serverId}.prev");
                if (File.Exists(targetPath)) File.Delete(targetPath);
                if (File.Exists(backupPath)) File.Delete(backupPath);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not delete profile files for server '{serverId}'", ex);
            }
        }
    }
}
