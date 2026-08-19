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

    public string Id { get => _id; set => SetProperty(ref _id, value); }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Region { get => _region; set => SetProperty(ref _region, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public string Endpoint { get => _endpoint; set => SetProperty(ref _endpoint, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public string PrivateKey { get => _privateKey; set => SetProperty(ref _privateKey, value); }
    public string PeerPublicKey { get => _peerPublicKey; set => SetProperty(ref _peerPublicKey, value); }
    public string Dns { get => _dns; set => SetProperty(ref _dns, value); }
    public int Mtu { get => _mtu; set => SetProperty(ref _mtu, value); }
    public string AllowedIps { get => _allowedIps; set => SetProperty(ref _allowedIps, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
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
    private int _generation = 1;
    private int _validityDays = 7;
    private string _privateKeyPem = string.Empty;
    private string _publicKeyPem = string.Empty;
    private string _statusMessage = "Ready";
    private string _serviceUrl = DCScreenSharing.Shared.Constants.DefaultProfileServiceUrl;
    private string _adminApiKey = "dev-admin-secret-key-replace-in-prod";
    private MaintainerServerItem? _selectedServer;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string _keysDirectory;

    public ObservableCollection<MaintainerServerItem> Servers { get; } = new();

    public int Generation { get => _generation; set => SetProperty(ref _generation, value); }
    public int ValidityDays { get => _validityDays; set => SetProperty(ref _validityDays, value); }
    public string PrivateKeyPem { get => _privateKeyPem; set => SetProperty(ref _privateKeyPem, value); }
    public string PublicKeyPem { get => _publicKeyPem; set => SetProperty(ref _publicKeyPem, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
    public string ServiceUrl { get => _serviceUrl; set => SetProperty(ref _serviceUrl, value); }
    public string AdminApiKey { get => _adminApiKey; set => SetProperty(ref _adminApiKey, value); }

    public MaintainerServerItem? SelectedServer
    {
        get => _selectedServer;
        set => SetProperty(ref _selectedServer, value);
    }

    public MaintainerViewModel()
    {
        _keysDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DCSS.Maintainer", "keys");
        Directory.CreateDirectory(_keysDirectory);

        LoadOrInitializeSigningKeys();
    }

    private void LoadOrInitializeSigningKeys()
    {
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

    public void GenerateAndSaveNewKeys()
    {
        var (priv, pub) = ProfileCrypto.GenerateKeyPair();
        PrivateKeyPem = priv;
        PublicKeyPem = pub;

        SaveSigningKeysInternal(priv, pub);
        StatusMessage = "Generated new persistent RSA-2048 signing keys (DPAPI protected).";
    }

    private void SaveSigningKeysInternal(string privKey, string pubKey)
    {
        var privKeyPath = Path.Combine(_keysDirectory, "maintainer_signing.key");
        var pubKeyPath = Path.Combine(_keysDirectory, "maintainer_signing.pub");

        var privBytes = Encoding.UTF8.GetBytes(privKey);
        var encryptedBytes = ProtectedData.Protect(privBytes, null, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(privKeyPath, encryptedBytes);
        File.WriteAllText(pubKeyPath, pubKey);
    }

    public void ImportConfIntoServer(MaintainerServerItem server, string confContent)
    {
        var parsed = WireGuardConfParser.Parse(confContent);
        server.PrivateKey = parsed.PrivateKey;
        server.PeerPublicKey = parsed.PeerPublicKey;
        server.Endpoint = parsed.Endpoint;
        server.Port = parsed.Port;
        server.Address = parsed.Address;
        server.Dns = parsed.Dns;
        server.Mtu = parsed.Mtu;
        server.AllowedIps = parsed.AllowedIps;

        StatusMessage = $"Imported configuration from .conf into '{server.Id}'.";
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
            if (string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Name))
                return (false, "Server ID and Name cannot be empty.");

            if (s.Enabled)
            {
                if (string.IsNullOrWhiteSpace(s.Endpoint) || s.Port <= 0 || s.Port > 65535)
                    return (false, $"Server '{s.Id}' has invalid endpoint or port.");

                if (string.IsNullOrWhiteSpace(s.PrivateKey) || string.IsNullOrWhiteSpace(s.PeerPublicKey))
                    return (false, $"Server '{s.Id}' is missing WireGuard keys.");
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
                    PrivateKey = s.PrivateKey,
                    PeerPublicKey = s.PeerPublicKey,
                    Dns = s.Dns,
                    Mtu = s.Mtu,
                    AllowedIps = s.AllowedIps
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
                StatusMessage = $"Published generation {manifest.Generation} successfully!";
                Generation++;
                return (true, StatusMessage);
            }
            else
            {
                StatusMessage = $"Publish failed ({response.StatusCode}): {responseContent}";
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
            var endpoint = ServiceUrl.TrimEnd('/') + "/api/v1/admin/tickets";
            var payload = JsonSerializer.Serialize(new { ValidityMinutes = validityMinutes, Description = description });

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Admin-Api-Key", AdminApiKey);

            var resp = await _httpClient.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                var ticketObj = JsonSerializer.Deserialize<TicketResponse>(json);
                if (ticketObj != null && !string.IsNullOrEmpty(ticketObj.Ticket))
                {
                    StatusMessage = $"Generated single-use ticket: {ticketObj.Ticket}";
                    return (true, ticketObj.Ticket, $"Ticket generated (expires in {validityMinutes} mins): {ticketObj.Ticket}");
                }
            }

            return (false, string.Empty, $"Failed to create ticket ({resp.StatusCode}): {json}");
        }
        catch (Exception ex)
        {
            return (false, string.Empty, ex.Message);
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
                StatusMessage = $"Successfully rolled back to generation {targetGeneration}.";
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
