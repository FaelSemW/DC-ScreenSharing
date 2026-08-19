using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DCScreenSharing.Core.Security;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.Profiles;

public class ChallengeResponse
{
    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("expiresInSeconds")]
    public int ExpiresInSeconds { get; set; }
}

public class EnrollmentResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class ProfileRotationCoordinator
{
    private readonly SecureProfileStore _store;
    private readonly IAppLogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _publicKeyPem;
    private readonly string _serviceBaseUrl;
    public ClientIdentity ClientIdentity { get; }

    public ProfileRotationCoordinator(
        SecureProfileStore store,
        IAppLogger logger,
        string? publicKeyPem = null,
        string? serviceBaseUrl = null,
        HttpClient? httpClient = null,
        ClientIdentity? clientIdentity = null)
    {
        _store = store;
        _logger = logger;
        _publicKeyPem = publicKeyPem ?? string.Empty;
        _serviceBaseUrl = (serviceBaseUrl ?? Constants.DefaultProfileServiceUrl).TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DC-ScreenSharing-Client/1.0");
        ClientIdentity = clientIdentity ?? new ClientIdentity(logger: logger);
    }

    public async Task<ServerCatalog?> FetchRemoteCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var catalogUrl = $"{_serviceBaseUrl}/api/v1/catalog";
            _logger.Info($"Fetching server catalog from {catalogUrl}...");
            var response = await _httpClient.GetStringAsync(catalogUrl, ct);

            if (response.Contains("signatureBase64") && response.Contains("catalogJson"))
            {
                var manifest = JsonSerializer.Deserialize<SignedManifest>(response);
                if (manifest != null)
                {
                    if (!string.IsNullOrEmpty(_publicKeyPem))
                    {
                        var verified = ProfileCrypto.VerifySignature(manifest.CatalogJson, manifest.SignatureBase64, _publicKeyPem);
                        if (!verified)
                        {
                            _logger.Error("Catalog manifest failed cryptographic signature verification! Rejecting.");
                            return null;
                        }
                    }

                    return JsonSerializer.Deserialize<ServerCatalog>(manifest.CatalogJson);
                }
            }

            var catalog = JsonSerializer.Deserialize<ServerCatalog>(response);
            if (catalog != null)
            {
                _logger.Info($"Retrieved catalog generation {catalog.Generation} with {catalog.Servers.Count} servers.");
            }
            return catalog;
        }
        catch (Exception ex)
        {
            _logger.Warning($"Could not retrieve remote server catalog: {ex.Message}");
            return null;
        }
    }

    public async Task<(bool Success, string Message)> EnrollWithTicketAsync(string enrollmentTicket, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(enrollmentTicket))
                return (false, "Enrollment ticket cannot be empty.");

            var enrollUrl = $"{_serviceBaseUrl}/api/v1/client/enroll";
            var req = new
            {
                enrollmentTicket = enrollmentTicket.Trim(),
                publicKeyPem = ClientIdentity.PublicKeyPem
            };

            var content = new StringContent(JsonSerializer.Serialize(req), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(enrollUrl, content, ct);
            var responseJson = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warning($"Enrollment rejected ({resp.StatusCode}): {responseJson}");
                return (false, $"Enrollment failed: {responseJson}");
            }

            var result = JsonSerializer.Deserialize<EnrollmentResponse>(responseJson);
            if (result != null && result.Success && !string.IsNullOrEmpty(result.ClientId))
            {
                ClientIdentity.SetEnrolledClientId(result.ClientId);
                _logger.Info($"Client successfully enrolled with ClientId: {result.ClientId}");
                return (true, "Enrollment successful.");
            }

            return (false, "Invalid server enrollment response.");
        }
        catch (Exception ex)
        {
            _logger.Error("Exception during client enrollment", ex);
            return (false, ex.Message);
        }
    }

    public async Task<ServerProfile?> GetOrRefreshProfileAsync(string serverId, CancellationToken ct = default)
    {
        var cached = _store.LoadProfile(serverId);
        var now = DateTime.UtcNow;

        if (cached != null)
        {
            var remainingHours = (cached.ExpiresAt - now).TotalHours;
            if (remainingHours > Constants.ProfileRenewalThresholdHours)
            {
                _logger.Info($"Using cached profile for server '{serverId}' (expires in {remainingHours:F1} hours).");
                return cached;
            }

            if (remainingHours > 0)
            {
                _logger.Info($"Profile for server '{serverId}' approaches expiration ({remainingHours:F1} hours remaining). Attempting background refresh...");
                var refreshed = await TryFetchProfileAsync(serverId, ct);
                return refreshed ?? cached;
            }

            _logger.Warning($"Cached profile for server '{serverId}' expired at {cached.ExpiresAt:u}. Attempting renewal...");
        }

        var fresh = await TryFetchProfileAsync(serverId, ct);
        if (fresh != null)
            return fresh;

        if (cached != null && cached.ExpiresAt > now)
        {
            return cached;
        }

        _logger.Error($"Unable to obtain a valid active profile for server '{serverId}'.");
        return null;
    }

    public async Task<ServerProfile?> TryFetchProfileAsync(string serverId, CancellationToken ct = default)
    {
        try
        {
            if (!ClientIdentity.IsEnrolled)
            {
                _logger.Warning("Cannot acquire profile: Client has not been enrolled with an enrollment ticket yet.");
                return null;
            }

            // 1. Request single-use challenge nonce
            var challengeUrl = $"{_serviceBaseUrl}/api/v1/client/challenge?clientId={ClientIdentity.ClientId}";
            var challengeJson = await _httpClient.GetStringAsync(challengeUrl, ct);
            var challenge = JsonSerializer.Deserialize<ChallengeResponse>(challengeJson);

            if (challenge == null || string.IsNullOrEmpty(challenge.Nonce))
            {
                _logger.Warning("Failed to obtain challenge nonce from ProfileService.");
                return null;
            }

            // 2. Compute cryptographic Proof of Possession
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payloadToSign = $"{ClientIdentity.ClientId}:{serverId}:{challenge.Nonce}:{timestamp}";
            var signature = ClientIdentity.SignPayload(payloadToSign);

            // 3. Acquire profile using Proof of Possession
            var profileUrl = $"{_serviceBaseUrl}/api/v1/servers/{serverId}/profile";
            _logger.Info($"Requesting active profile for server '{serverId}' with cryptographic Proof-of-Possession...");

            var acquireReq = new
            {
                clientId = ClientIdentity.ClientId,
                nonce = challenge.Nonce,
                timestamp = timestamp,
                signatureBase64 = signature
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(acquireReq), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(profileUrl, jsonContent, ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.Warning($"Profile request rejected by ProfileService: {resp.StatusCode}");
                return null;
            }

            var responseJson = await resp.Content.ReadAsStringAsync(ct);
            var profile = JsonSerializer.Deserialize<ServerProfile>(responseJson);

            if (profile != null && profile.ExpiresAt > DateTime.UtcNow)
            {
                _store.SaveProfile(profile);
                _logger.Info($"Successfully retrieved and securely stored profile for '{serverId}' (Gen {profile.Generation}).");
                return profile;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Failed to fetch profile for server '{serverId}': {ex.Message}");
        }

        return null;
    }
}
