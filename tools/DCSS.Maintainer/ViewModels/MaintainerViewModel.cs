using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCScreenSharing.Core.Profiles;

namespace DCSS.Maintainer.ViewModels;

public class MaintainerServerItem : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _region = "US";
    private bool _enabled = true;
    private string _endpoint = "vpn.example.com";
    private int _port = 51820;
    private string _address = "10.8.0.2/32";
    private string _privateKey = string.Empty;
    private string _peerPublicKey = string.Empty;
    private string _dns = "1.1.1.1, 8.8.8.8";
    private int _mtu = 1420;
    private string _allowedIps = "0.0.0.0/0, ::/0";
    private List<string> _addresses = new();
    private List<string> _dnsServers = new();
    private List<string> _allowedIpsList = new();
    private int _persistentKeepalive = 25;
    private string? _sourceConfPath;
    private string _status = "Ready";

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Region { get => _region; set => SetProperty(ref _region, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public List<string> Addresses { get => _addresses; set => SetProperty(ref _addresses, value); }
    public string PrivateKey { get => _privateKey; set => SetProperty(ref _privateKey, value); }
    public string PeerPublicKey { get => _peerPublicKey; set => SetProperty(ref _peerPublicKey, value); }
    public string Dns { get => _dns; set => SetProperty(ref _dns, value); }
    public List<string> DnsServers { get => _dnsServers; set => SetProperty(ref _dnsServers, value); }
    public int Mtu { get => _mtu; set => SetProperty(ref _mtu, value); }
    public string AllowedIps { get => _allowedIps; set => SetProperty(ref _allowedIps, value); }
    public List<string> AllowedIpsList { get => _allowedIpsList; set => SetProperty(ref _allowedIpsList, value); }
    public int PersistentKeepalive { get => _persistentKeepalive; set => SetProperty(ref _persistentKeepalive, value); }
    public string? SourceConfPath { get => _sourceConfPath; set => SetProperty(ref _sourceConfPath, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public bool HasValidProfile => !string.IsNullOrWhiteSpace(PrivateKey) && !string.IsNullOrWhiteSpace(PeerPublicKey);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public class MaintainerSettings
{
    public string ServiceUrl { get; set; } = "https://zaprecovery.online";
    public int ValidityDays { get; set; } = 7;
    public List<ServerMetadataRecord> Servers { get; set; } = new();
}

public class ServerMetadataRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Region { get; set; } = "US";
    public bool Enabled { get; set; } = true;
    public string? SourceConfPath { get; set; }
}

public class MaintainerSecrets
{
    public string AdminApiKey { get; set; } = string.Empty;
    public Dictionary<string, string> ServerPrivateKeys { get; set; } = new();
}

public class TicketResponse
{
    [JsonPropertyName("ticket")]
    public string Ticket { get; set; } = string.Empty;

    [JsonPropertyName("ticketHash")]
    public string TicketHash { get; set; } = string.Empty;

    [JsonPropertyName("expiresAtUtc")]
    public DateTime ExpiresAtUtc { get; set; }

    [JsonPropertyName("validityMinutes")]
    public int ValidityMinutes { get; set; }
}

public class MaintainerViewModel : INotifyPropertyChanged
{
    private int _activeGeneration = 0;
    private int _generation = 1;
    private int _validityDays = 7;
    private string _privateKeyPem = string.Empty;
    private string _publicKeyPem = string.Empty;
    private string _statusMessage = "Ready";
    private string _serviceUrl = "https://zaprecovery.online";
    private string _adminApiKey = string.Empty;
    private bool _showAdminApiKey = false;
    private MaintainerServerItem? _selectedServer;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _baseDirectory;
    private readonly string _keysDirectory;
    private readonly string _settingsFilePath;
    private readonly string _secretsFilePath;
    private readonly object _stateLock = new();

    public ObservableCollection<MaintainerServerItem> Servers { get; } = new();

    public int ActiveGeneration
    {
        get => _activeGeneration;
        set
        {
            if (SetProperty(ref _activeGeneration, value))
            {
                OnPropertyChanged(nameof(GenerationDisplay));
            }
        }
    }

    public int Generation
    {
        get => _generation;
        set
        {
            if (SetProperty(ref _generation, value))
            {
                OnPropertyChanged(nameof(GenerationDisplay));
            }
        }
    }

    public string GenerationDisplay =>
        ActiveGeneration > 0
            ? $"Current Active: {ActiveGeneration} | Next: {Generation}"
            : $"Current Active: Querying... | Next: {Generation}";

    public int ValidityDays
    {
        get => _validityDays;
        set
        {
            if (SetProperty(ref _validityDays, value))
            {
                SaveSettings();
            }
        }
    }

    public string PrivateKeyPem
    {
        get => _privateKeyPem;
        set => SetProperty(ref _privateKeyPem, value);
    }

    public string PublicKeyPem
    {
        get => _publicKeyPem;
        set => SetProperty(ref _publicKeyPem, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string ServiceUrl
    {
        get => _serviceUrl;
        set
        {
            if (SetProperty(ref _serviceUrl, value))
            {
                SaveSettings();
            }
        }
    }

    public string AdminApiKey
    {
        get => _adminApiKey;
        set
        {
            if (SetProperty(ref _adminApiKey, value))
            {
                SaveSecrets();
            }
        }
    }

    public bool ShowAdminApiKey
    {
        get => _showAdminApiKey;
        set => SetProperty(ref _showAdminApiKey, value);
    }

    public MaintainerServerItem? SelectedServer
    {
        get => _selectedServer;
        set => SetProperty(ref _selectedServer, value);
    }

    public string SettingsFilePath => _settingsFilePath;
    public string SecretsFilePath => _secretsFilePath;
    public string KeysDirectory => _keysDirectory;

    public MaintainerViewModel()
    {
        _baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DCSS.Maintainer");
        _keysDirectory = Path.Combine(_baseDirectory, "keys");
        _settingsFilePath = Path.Combine(_baseDirectory, "settings.json");
        _secretsFilePath = Path.Combine(_baseDirectory, "secrets.dat");

        Directory.CreateDirectory(_baseDirectory);
        Directory.CreateDirectory(_keysDirectory);

        LoadOrInitializeSigningKeys();
        LoadSavedSettingsAndSecrets();

        // Automatically query ProfileService for current active generation on startup
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshActiveGenerationAsync();
            }
            catch { }
        });
    }

    private void LoadOrInitializeSigningKeys()
    {
        try
        {
            Directory.CreateDirectory(_keysDirectory);
            var privKeyPath = Path.Combine(_keysDirectory, "maintainer_signing.key");
            var pubKeyPath = Path.Combine(_keysDirectory, "maintainer_signing.pub");

            if (File.Exists(privKeyPath) && File.Exists(pubKeyPath))
            {
                try
                {
                    var fileBytes = File.ReadAllBytes(privKeyPath);
                    try
                    {
                        var decryptedBytes = ProtectedData.Unprotect(fileBytes, null, DataProtectionScope.CurrentUser);
                        PrivateKeyPem = Encoding.UTF8.GetString(decryptedBytes);
                    }
                    catch
                    {
                        var plainText = Encoding.UTF8.GetString(fileBytes);
                        if (plainText.Contains("BEGIN RSA PRIVATE KEY") || plainText.Contains("BEGIN PRIVATE KEY"))
                        {
                            PrivateKeyPem = plainText;
                            SaveSigningKeysInternal(plainText, File.ReadAllText(pubKeyPath));
                        }
                    }

                    PublicKeyPem = File.ReadAllText(pubKeyPath);
                    StatusMessage = "Loaded persistent signing key pair (DPAPI protected).";
                    return;
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Could not load existing keys: {ex.Message}";
                }
            }

            GenerateAndSaveNewKeys();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Signing key initialization notice: {ex.Message}";
        }
    }

    public void GenerateAndSaveNewKeys()
    {
        try
        {
            var (priv, pub) = ProfileCrypto.GenerateKeyPair();
            PrivateKeyPem = priv;
            PublicKeyPem = pub;

            SaveSigningKeysInternal(priv, pub);
            StatusMessage = "Generated new persistent RSA-2048 signing keys (DPAPI protected).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error generating signing keys: {ex.Message}";
        }
    }

    private void SaveSigningKeysInternal(string privKey, string pubKey)
    {
        try
        {
            Directory.CreateDirectory(_keysDirectory);
            var privKeyPath = Path.Combine(_keysDirectory, "maintainer_signing.key");
            var pubKeyPath = Path.Combine(_keysDirectory, "maintainer_signing.pub");

            var privBytes = Encoding.UTF8.GetBytes(privKey);
            var encryptedBytes = ProtectedData.Protect(privBytes, null, DataProtectionScope.CurrentUser);

            File.WriteAllBytes(privKeyPath, encryptedBytes);
            File.WriteAllText(pubKeyPath, pubKey);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save signing keys: {ex.Message}";
        }
    }

    public void LoadSavedSettingsAndSecrets()
    {
        lock (_stateLock)
        {
            MaintainerSettings? settings = null;
            MaintainerSecrets? secrets = null;

            // 1. Load non-secret settings
            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    settings = JsonSerializer.Deserialize<MaintainerSettings>(json);
                }
                catch { }
            }

            if (settings != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.ServiceUrl))
                    _serviceUrl = settings.ServiceUrl;

                if (settings.ValidityDays > 0)
                    _validityDays = settings.ValidityDays;
            }

            // 2. Load and decrypt secrets via DPAPI CurrentUser
            if (File.Exists(_secretsFilePath))
            {
                try
                {
                    var encryptedBytes = File.ReadAllBytes(_secretsFilePath);
                    var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                    var secretsJson = Encoding.UTF8.GetString(decryptedBytes);
                    secrets = JsonSerializer.Deserialize<MaintainerSecrets>(secretsJson);
                }
                catch { }
            }

            if (secrets != null)
            {
                if (!string.IsNullOrEmpty(secrets.AdminApiKey))
                    _adminApiKey = secrets.AdminApiKey;
            }

            // 3. Restore server list
            Servers.Clear();
            if (settings?.Servers != null && settings.Servers.Count > 0)
            {
                foreach (var sMeta in settings.Servers)
                {
                    var server = new MaintainerServerItem
                    {
                        Id = sMeta.Id,
                        Name = sMeta.Name,
                        Region = sMeta.Region,
                        Enabled = sMeta.Enabled,
                        SourceConfPath = sMeta.SourceConfPath
                    };

                    // Try to re-parse from remembered source .conf path
                    var confLoaded = false;
                    if (!string.IsNullOrEmpty(sMeta.SourceConfPath) && File.Exists(sMeta.SourceConfPath))
                    {
                        try
                        {
                            var confText = File.ReadAllText(sMeta.SourceConfPath);
                            ImportConfIntoServer(server, confText);
                            server.SourceConfPath = sMeta.SourceConfPath;
                            server.Status = "Ready (.conf loaded)";
                            confLoaded = true;
                        }
                        catch { }
                    }

                    // Fallback to decrypted cache if available
                    if (!confLoaded && secrets?.ServerPrivateKeys != null && secrets.ServerPrivateKeys.TryGetValue(server.Id, out var cachedPrivKey))
                    {
                        server.PrivateKey = cachedPrivKey;
                        server.Status = "Ready (Decrypted cache)";
                    }
                    else if (!confLoaded)
                    {
                        server.Status = "Profile file missing — Replace from .conf required";
                    }

                    Servers.Add(server);
                }
            }

            SelectedServer = Servers.FirstOrDefault();
        }
    }

    public void SaveSettings()
    {
        lock (_stateLock)
        {
            try
            {
                var settings = new MaintainerSettings
                {
                    ServiceUrl = ServiceUrl,
                    ValidityDays = ValidityDays,
                    Servers = Servers.Select(s => new ServerMetadataRecord
                    {
                        Id = s.Id,
                        Name = s.Name,
                        Region = s.Region,
                        Enabled = s.Enabled,
                        SourceConfPath = s.SourceConfPath
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not save settings: {ex.Message}";
            }
        }
    }

    public void SaveSecrets()
    {
        lock (_stateLock)
        {
            try
            {
                var secrets = new MaintainerSecrets
                {
                    AdminApiKey = AdminApiKey,
                    ServerPrivateKeys = Servers
                        .Where(s => !string.IsNullOrEmpty(s.PrivateKey))
                        .ToDictionary(s => s.Id, s => s.PrivateKey)
                };

                var secretsJson = JsonSerializer.Serialize(secrets);
                var plainBytes = Encoding.UTF8.GetBytes(secretsJson);
                var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);

                File.WriteAllBytes(_secretsFilePath, encryptedBytes);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not save encrypted credentials: {ex.Message}";
            }
        }
    }

    public void ClearSavedAdminCredentials()
    {
        lock (_stateLock)
        {
            AdminApiKey = string.Empty;
            try
            {
                if (File.Exists(_secretsFilePath))
                {
                    File.Delete(_secretsFilePath);
                }
                StatusMessage = "Saved admin credentials cleared securely.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error clearing credentials: {ex.Message}";
            }
        }
    }

    public void ImportConfIntoServer(MaintainerServerItem server, string confContent, string? confFilePath = null)
    {
        var parsed = WireGuardConfParser.Parse(confContent);
        server.PrivateKey = parsed.PrivateKey;
        server.PeerPublicKey = parsed.PeerPublicKey;
        server.Endpoint = parsed.Endpoint;
        server.Port = parsed.Port;
        server.Address = parsed.Address;
        server.Addresses = new List<string>(parsed.Addresses);
        server.Dns = parsed.Dns;
        server.DnsServers = new List<string>(parsed.DnsServers);
        server.Mtu = parsed.Mtu;
        server.AllowedIps = parsed.AllowedIps;
        server.AllowedIpsList = new List<string>(parsed.AllowedIpsList);
        server.PersistentKeepalive = parsed.PersistentKeepalive;

        if (!string.IsNullOrEmpty(confFilePath))
        {
            server.SourceConfPath = confFilePath;
        }

        server.Status = "Ready (.conf loaded)";
        StatusMessage = $"Imported configuration from .conf into '{server.Id}'.";

        SaveSettings();
        SaveSecrets();
    }

    public async Task<(bool Success, int ActiveGeneration, string Message)> RefreshActiveGenerationAsync(bool updateNextGeneration = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ServiceUrl))
            {
                StatusMessage = "ProfileService URL is empty.";
                return (false, 0, StatusMessage);
            }

            var endpoint = ServiceUrl.TrimEnd('/') + "/api/v1/health";
            var resp = await _httpClient.GetAsync(endpoint);

            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("activeGeneration", out var genProp))
                {
                    var activeGen = genProp.GetInt32();
                    ActiveGeneration = activeGen;
                    if (updateNextGeneration)
                    {
                        Generation = activeGen + 1;
                    }
                    StatusMessage = $"ProfileService online. Active Generation: {activeGen}, Next Generation: {activeGen + 1}";
                    return (true, activeGen, StatusMessage);
                }
            }

            // Fallback to /api/v1/catalog
            var catalogEndpoint = ServiceUrl.TrimEnd('/') + "/api/v1/catalog";
            var catResp = await _httpClient.GetAsync(catalogEndpoint);
            if (catResp.IsSuccessStatusCode)
            {
                var catJson = await catResp.Content.ReadAsStringAsync();
                var catalog = JsonSerializer.Deserialize<ServerCatalog>(catJson);
                if (catalog != null)
                {
                    ActiveGeneration = catalog.Generation;
                    if (updateNextGeneration)
                    {
                        Generation = catalog.Generation + 1;
                    }
                    StatusMessage = $"Catalog online. Active Generation: {catalog.Generation}, Next Generation: {catalog.Generation + 1}";
                    return (true, catalog.Generation, StatusMessage);
                }
            }

            StatusMessage = $"Could not determine active generation ({resp.StatusCode}).";
            return (false, 0, StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unable to reach ProfileService at {ServiceUrl}: {ex.Message}";
            return (false, 0, StatusMessage);
        }
    }

    public (bool Success, string Message) ValidateAll()
    {
        if (string.IsNullOrWhiteSpace(PrivateKeyPem))
            return (false, "Private signing key is missing.");

        if (Servers.Count == 0)
            return (false, "At least one server must be defined.");

        var duplicateIds = Servers.GroupBy(s => s.Id.ToLowerInvariant()).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateIds.Count > 0)
            return (false, $"Duplicate server IDs found: {string.Join(", ", duplicateIds)}");

        foreach (var s in Servers)
        {
            if (s.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Endpoint) || s.Port <= 0 || s.Port > 65535)
                    return (false, $"Server '{s.Id}' has invalid endpoint or port.");

                if (string.IsNullOrWhiteSpace(s.PrivateKey) || string.IsNullOrWhiteSpace(s.PeerPublicKey))
                    return (false, $"Server '{s.Id}' is missing WireGuard keys ({s.Status}).");
            }
        }

        return (true, "All validations passed.");
    }

    public SignedManifest BuildSignedManifest()
    {
        var validation = ValidateAll();
        if (!validation.Success)
            throw new InvalidOperationException(validation.Message);

        var now = DateTime.UtcNow;
        var expires = now.AddDays(ValidityDays);

        var catalog = new ServerCatalog
        {
            Schema = 1,
            Generation = Generation,
            PublishedAtUtc = now,
            Servers = Servers.Select(s => new ServerEntry
            {
                Id = s.Id,
                Name = s.Name,
                Region = s.Region,
                Enabled = s.Enabled
            }).ToList()
        };

        var catalogJson = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        var catalogSig = ProfileCrypto.SignData(catalogJson, PrivateKeyPem);

        var manifest = new SignedManifest
        {
            CatalogJson = catalogJson,
            SignatureBase64 = catalogSig,
            Generation = Generation,
            PublishedAtUtc = now
        };

        foreach (var s in Servers.Where(s => s.Enabled))
        {
            var profile = new ServerProfile
            {
                ProfileId = Guid.NewGuid().ToString("N"),
                ServerId = s.Id,
                Generation = Generation,
                IssuedAt = now,
                ExpiresAt = expires,
                SchemaVersion = 1,
                Wireguard = new WireGuardProfileConfig
                {
                    Endpoint = s.Endpoint,
                    Port = s.Port,
                    Address = s.Address,
                    Addresses = new List<string>(s.Addresses),
                    PrivateKey = s.PrivateKey,
                    PeerPublicKey = s.PeerPublicKey,
                    Dns = s.Dns,
                    DnsServers = new List<string>(s.DnsServers),
                    Mtu = s.Mtu,
                    AllowedIps = s.AllowedIps,
                    AllowedIpsList = new List<string>(s.AllowedIpsList),
                    PersistentKeepalive = s.PersistentKeepalive
                }
            };

            var profileJson = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            var profileSig = ProfileCrypto.SignData(profileJson, PrivateKeyPem);

            manifest.ProfilePayloads[s.Id] = profileJson;
            manifest.ProfileSignatures[s.Id] = profileSig;
        }

        return manifest;
    }

    public async Task<(bool Success, string Message)> PublishToServiceAsync()
    {
        try
        {
            StatusMessage = "Verifying remote active generation with ProfileService...";

            var attemptedGen = Generation;

            // 1. Immediately refresh remote active generation before publish
            var (syncOk, remoteActiveGen, _) = await RefreshActiveGenerationAsync(updateNextGeneration: false);
            if (syncOk)
            {
                if (attemptedGen <= remoteActiveGen)
                {
                    Generation = remoteActiveGen + 1;
                    StatusMessage = $"Cannot publish generation {attemptedGen}. Ensure generation number is strictly greater than current active generation ({remoteActiveGen}). Next generation has been automatically adjusted to {Generation}. Please review and click Publish again.";
                    return (false, StatusMessage);
                }
            }

            var validation = ValidateAll();
            if (!validation.Success)
            {
                return (false, validation.Message);
            }

            StatusMessage = "Building and signing manifest...";
            var manifest = BuildSignedManifest();

            var endpoint = ServiceUrl.TrimEnd('/') + "/api/v1/admin/publish";
            StatusMessage = $"Publishing generation {manifest.Generation} to {endpoint}...";

            var json = JsonSerializer.Serialize(manifest);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Admin-Api-Key", AdminApiKey);

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                ActiveGeneration = manifest.Generation;
                Generation = manifest.Generation + 1;
                StatusMessage = $"Published generation {manifest.Generation} successfully! Current Generation: {manifest.Generation}, Next Generation: {manifest.Generation + 1}";
                
                SaveSettings();
                SaveSecrets();
                return (true, StatusMessage);
            }
            else
            {
                // Refresh remote state to capture any newly active generation on server
                await RefreshActiveGenerationAsync();
                StatusMessage = $"Publish failed ({response.StatusCode}): {responseContent}\nRemote active generation is now {ActiveGeneration}. Next generation is {Generation}.";
                return (false, StatusMessage);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Network error during publication: {ex.Message}";
            return (false, StatusMessage);
        }
    }

    public async Task<(bool Success, string Ticket, string Message)> GenerateEnrollmentTicketAsync(int validityMinutes = 30, string description = "Client Enrollment")
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ServiceUrl))
            {
                StatusMessage = "ProfileService URL is empty. Please enter a valid URL.";
                return (false, string.Empty, StatusMessage);
            }

            if (string.IsNullOrWhiteSpace(AdminApiKey))
            {
                StatusMessage = "Admin API Key is missing. Please enter your Admin API Key.";
                return (false, string.Empty, StatusMessage);
            }

            var endpoint = ServiceUrl.TrimEnd('/') + "/api/v1/admin/tickets";
            var payload = JsonSerializer.Serialize(new { ValidityMinutes = validityMinutes, Description = description });

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Admin-Api-Key", AdminApiKey);

            using var resp = await _httpClient.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    var ticketObj = JsonSerializer.Deserialize<TicketResponse>(json);
                    if (ticketObj != null && !string.IsNullOrEmpty(ticketObj.Ticket))
                    {
                        StatusMessage = $"Generated single-use ticket: {ticketObj.Ticket}";
                        return (true, ticketObj.Ticket, $"Ticket generated (expires in {validityMinutes} mins): {ticketObj.Ticket}");
                    }
                }
                catch (JsonException)
                {
                    StatusMessage = "Received invalid JSON from ProfileService.";
                    return (false, string.Empty, StatusMessage);
                }
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                StatusMessage = "Access denied: Invalid or unauthorized Admin API Key.";
                return (false, string.Empty, StatusMessage);
            }
            else
            {
                StatusMessage = $"Failed to create ticket (Status: {(int)resp.StatusCode} {resp.StatusCode}): {json}";
                return (false, string.Empty, StatusMessage);
            }

            StatusMessage = $"Unable to create ticket (Status: {(int)resp.StatusCode}).";
            return (false, string.Empty, StatusMessage);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Unable to reach ProfileService at {ServiceUrl}: {ex.Message}";
            return (false, string.Empty, StatusMessage);
        }
        catch (TaskCanceledException)
        {
            StatusMessage = "Request to ProfileService timed out.";
            return (false, string.Empty, StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error creating ticket: {ex.Message}";
            return (false, string.Empty, StatusMessage);
        }
    }

    public async Task<(bool Success, string Message)> RollbackServiceAsync(int targetGeneration)
    {
        try
        {
            var endpoint = ServiceUrl.TrimEnd('/') + "/api/v1/admin/rollback";
            StatusMessage = $"Rolling back to generation {targetGeneration} on {endpoint}...";

            var payload = JsonSerializer.Serialize(new { TargetGeneration = targetGeneration });
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Admin-Api-Key", AdminApiKey);

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                await RefreshActiveGenerationAsync();
                StatusMessage = $"Successfully rolled back to generation {targetGeneration}. Current active generation: {ActiveGeneration}.";
                return (true, StatusMessage);
            }
            else
            {
                StatusMessage = $"Rollback failed: {responseContent}";
                return (false, StatusMessage);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error during rollback: {ex.Message}";
            return (false, StatusMessage);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
