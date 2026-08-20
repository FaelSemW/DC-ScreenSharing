using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;
using DCSS.ProfileCollector.Models;

namespace DCSS.ProfileCollector.Services;

public class ProfileStorageService
{
    private readonly string _inventoryFilePath;
    private readonly object _inventoryLock = new();

    public string InventoryFilePath => _inventoryFilePath;

    public ProfileStorageService(string? customInventoryPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customInventoryPath))
        {
            _inventoryFilePath = customInventoryPath;
        }
        else
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DCSS.ProfileCollector");
            Directory.CreateDirectory(appData);
            _inventoryFilePath = Path.Combine(appData, "inventory.json");
        }
    }

    public static string GetDefaultConfigsRoot()
    {
        // Check current directory and up to 3 parent directories for a 'configs' folder or solution root
        var current = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(current);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DC-ScreenSharing.sln")) || Directory.Exists(Path.Combine(dir.FullName, "configs")))
            {
                return Path.Combine(dir.FullName, "configs");
            }
            dir = dir.Parent;
        }

        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs");
    }

    public static string GetRegionFolder(string configsRoot, string regionCode, string provider = ProviderConstants.ProtonVpn)
    {
        string folder;
        if (string.Equals(provider, ProviderConstants.ProtonVpn, StringComparison.OrdinalIgnoreCase))
        {
            folder = Path.Combine(configsRoot, "Proton", regionCode.ToUpperInvariant());
        }
        else
        {
            folder = Path.Combine(configsRoot, regionCode.ToUpperInvariant());
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    public static int GetNextConfigNumber(string targetFolder, string regionCode)
    {
        if (!Directory.Exists(targetFolder))
        {
            Directory.CreateDirectory(targetFolder);
            return 1;
        }

        var prefix = regionCode.ToLowerInvariant();
        var regex = new Regex($@"^{prefix}-(\d+)\.conf$", RegexOptions.IgnoreCase);

        int maxNumber = 0;
        var files = Directory.GetFiles(targetFolder, "*.conf");
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var match = regex.Match(fileName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var num))
            {
                if (num > maxNumber)
                {
                    maxNumber = num;
                }
            }
        }

        return maxNumber + 1;
    }

    public static int GetExistingConfigCount(string targetFolder, string regionCode)
    {
        if (!Directory.Exists(targetFolder)) return 0;
        var prefix = regionCode.ToLowerInvariant();
        var regex = new Regex($@"^{prefix}-(\d+)\.conf$", RegexOptions.IgnoreCase);
        return Directory.GetFiles(targetFolder, "*.conf")
            .Select(Path.GetFileName)
            .Count(f => f != null && regex.IsMatch(f));
    }

    public static string FormatConfigFileName(string regionCode, int number)
    {
        var prefix = regionCode.ToLowerInvariant();
        return $"{prefix}-{number:D3}.conf";
    }

    public static string DerivePublicIdentityHash(string peerPublicKey, string address, string endpoint, int port)
    {
        var rawIdentity = $"{peerPublicKey.Trim()}|{address.Trim()}|{endpoint.Trim()}:{port}";
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawIdentity));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static (bool IsValid, ParsedWireGuardConfig? Config, string ErrorMessage) ValidateConfigContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (false, null, "Configuration file is empty (zero-byte).");
        }

        var trimmed = content.Trim();
        if (trimmed.StartsWith("<", StringComparison.OrdinalIgnoreCase) || 
            trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "Downloaded content is an HTML error page, not a WireGuard configuration.");
        }

        try
        {
            var parsed = WireGuardConfParser.Parse(content);

            if (string.IsNullOrWhiteSpace(parsed.PrivateKey))
            {
                return (false, null, "Missing or invalid [Interface] PrivateKey.");
            }

            if (parsed.Addresses.Count == 0 && string.IsNullOrWhiteSpace(parsed.Address))
            {
                return (false, null, "Missing or invalid [Interface] Address.");
            }

            if (string.IsNullOrWhiteSpace(parsed.PeerPublicKey))
            {
                return (false, null, "Missing or invalid [Peer] PublicKey.");
            }

            if (string.IsNullOrWhiteSpace(parsed.Endpoint))
            {
                return (false, null, "Missing or invalid [Peer] Endpoint.");
            }

            if (parsed.Port <= 0 || parsed.Port > 65535)
            {
                return (false, null, $"Invalid port number ({parsed.Port}).");
            }

            // Real Engine Validation with sing-box 1.13.19
            var tunnelConfig = new TunnelConfiguration
            {
                ServerId = "collector-val",
                ServerName = "Validation Server",
                Endpoint = parsed.Endpoint,
                Port = parsed.Port,
                Addresses = new List<string>(parsed.Addresses),
                DnsServers = new List<string>(parsed.DnsServers),
                AllowedIpsList = new List<string>(parsed.AllowedIpsList),
                PrivateKey = parsed.PrivateKey,
                PeerPublicKey = parsed.PeerPublicKey,
                PersistentKeepalive = parsed.PersistentKeepalive,
                Mtu = parsed.Mtu
            };

            var tempValDir = Path.Combine(Path.GetTempPath(), "DCSS_Val_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempValDir);
            try
            {
                var engine = new ProcessRoutingEngine(new FileLogger(tempValDir), tempValDir);
                var (isEngineValid, engineError) = engine.ValidateRuntimeConfiguration(tunnelConfig);
                if (!isEngineValid)
                {
                    return (false, null, $"sing-box 1.13.19 configuration validation failed: {engineError}");
                }
            }
            finally
            {
                try { Directory.Delete(tempValDir, true); } catch { }
            }

            return (true, parsed, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to parse WireGuard configuration: {ex.Message}");
        }
    }

    public (bool Success, string FinalFilePath, string Filename, string IdentityHash, string Message, bool IsDuplicate) SaveValidatedProfile(
        string rawContent,
        string targetFolder,
        string regionCode,
        string serverName,
        DateTime? expiresAtUtc = null,
        string provider = ProviderConstants.ProtonVpn)
    {
        var validation = ValidateConfigContent(rawContent);
        if (!validation.IsValid || validation.Config == null)
        {
            return (false, string.Empty, string.Empty, string.Empty, validation.ErrorMessage, false);
        }

        var identityHash = DerivePublicIdentityHash(
            validation.Config.PeerPublicKey,
            validation.Config.Address,
            validation.Config.Endpoint,
            validation.Config.Port);

        // Check if duplicate identity exists in inventory or destination folder
        if (IsDuplicateIdentity(identityHash, targetFolder))
        {
            return (false, string.Empty, string.Empty, identityHash, $"Duplicate client identity detected ({identityHash.Substring(0, 8)}...). Quarantined and skipped.", true);
        }

        Directory.CreateDirectory(targetFolder);

        // Calculate next number safely
        var nextNumber = GetNextConfigNumber(targetFolder, regionCode);
        var filename = FormatConfigFileName(regionCode, nextNumber);
        var finalPath = Path.Combine(targetFolder, filename);

        // Atomic file write using temp file
        var tempDir = Path.Combine(Path.GetTempPath(), "DCSS.ProfileCollector");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, $"{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempFile, rawContent.Trim() + Environment.NewLine, Encoding.UTF8);

            // Atomic move to destination
            if (File.Exists(finalPath))
            {
                // Double safety check
                nextNumber = GetNextConfigNumber(targetFolder, regionCode);
                filename = FormatConfigFileName(regionCode, nextNumber);
                finalPath = Path.Combine(targetFolder, filename);
            }

            File.Move(tempFile, finalPath);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            return (false, string.Empty, string.Empty, identityHash, $"Failed to write configuration file: {ex.Message}", false);
        }

        // Record in inventory (without private keys)
        RecordInventoryItem(new InventoryItem
        {
            Provider = provider,
            Filename = filename,
            Region = regionCode.ToUpperInvariant(),
            Server = serverName,
            GeneratedUtc = DateTime.UtcNow,
            ValidationStatus = "Valid",
            DerivedPublicIdentityHash = identityHash,
            ExpiresAtUtc = expiresAtUtc
        });

        return (true, finalPath, filename, identityHash, "Ready", false);
    }

    public bool IsDuplicateIdentity(string identityHash, string targetFolder)
    {
        lock (_inventoryLock)
        {
            var db = LoadInventory();
            if (db.Items.Any(i => string.Equals(i.DerivedPublicIdentityHash, identityHash, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // Also inspect existing files in target folder
        if (Directory.Exists(targetFolder))
        {
            var confFiles = Directory.GetFiles(targetFolder, "*.conf");
            foreach (var file in confFiles)
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var parsed = WireGuardConfParser.Parse(text);
                    if (!string.IsNullOrWhiteSpace(parsed.PeerPublicKey) && !string.IsNullOrWhiteSpace(parsed.Endpoint))
                    {
                        var h = DerivePublicIdentityHash(parsed.PeerPublicKey, parsed.Address, parsed.Endpoint, parsed.Port);
                        if (string.Equals(h, identityHash, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch { }
            }
        }

        return false;
    }

    public InventoryDatabase LoadInventory()
    {
        lock (_inventoryLock)
        {
            try
            {
                if (!File.Exists(_inventoryFilePath))
                {
                    return new InventoryDatabase();
                }

                var json = File.ReadAllText(_inventoryFilePath);
                return JsonSerializer.Deserialize<InventoryDatabase>(json) ?? new InventoryDatabase();
            }
            catch
            {
                return new InventoryDatabase();
            }
        }
    }

    public void RecordInventoryItem(InventoryItem item)
    {
        lock (_inventoryLock)
        {
            try
            {
                var db = LoadInventory();
                db.LastUpdatedUtc = DateTime.UtcNow;
                db.Items.Add(item);

                var dir = Path.GetDirectoryName(_inventoryFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(db, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_inventoryFilePath, json, Encoding.UTF8);
            }
            catch { }
        }
    }
}
