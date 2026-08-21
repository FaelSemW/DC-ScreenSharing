using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DCScreenSharing.Core.Profiles;
using DCSS.ProfileService.Controllers;
using DCSS.ProfileService.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DC_ScreenSharing.IntegrationTests;

public class AdminWebsiteTests : IClassFixture<WebApplicationFactory<global::Program>>, IDisposable
{
    private readonly WebApplicationFactory<global::Program> _factory;
    private readonly HttpClient _client;
    private readonly string _testStorageDir;
    private const string TestAdminApiKey = "test-admin-secret-key-12345";

    public AdminWebsiteTests(WebApplicationFactory<global::Program> factory)
    {
        _testStorageDir = Path.Combine(Path.GetTempPath(), "DCSS_AdminTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageDir);
        Environment.SetEnvironmentVariable("ProfileService__StoragePath", _testStorageDir);
        Environment.SetEnvironmentVariable("ADMIN_API_KEY", TestAdminApiKey);

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((ctx, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ADMIN_API_KEY"] = TestAdminApiKey,
                    ["Admin:ApiKey"] = TestAdminApiKey,
                    ["ProfileService:StoragePath"] = _testStorageDir
                });
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testStorageDir))
            {
                Directory.Delete(_testStorageDir, true);
            }
        }
        catch { }
    }

    private async Task AuthenticateAdminAsync()
    {
        var loginResp = await _client.PostAsJsonAsync("/api/v1/admin/auth/login", new AdminLoginRequest
        {
            ApiKey = TestAdminApiKey
        });
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
    }

    [Fact]
    public async Task Auth_CorrectApiKey_SetsCookieAndReturnsSuccess()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/login", new AdminLoginRequest
        {
            ApiKey = TestAdminApiKey
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("success", body);

        // Verify session endpoint
        var sessionResp = await _client.GetAsync("/api/v1/admin/auth/session");
        Assert.Equal(HttpStatusCode.OK, sessionResp.StatusCode);
        var sessionBody = await sessionResp.Content.ReadAsStringAsync();
        Assert.Contains("\"authenticated\":true", sessionBody);
    }

    [Fact]
    public async Task Auth_IncorrectApiKey_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/login", new AdminLoginRequest
        {
            ApiKey = "wrong-key"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid administrator credentials", body);
    }

    [Fact]
    public async Task Auth_EmptyApiKey_ReturnsUnauthorized()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/auth/login", new AdminLoginRequest
        {
            ApiKey = ""
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_UnauthenticatedAdminEndpoint_ReturnsUnauthorized()
    {
        using var unauthClient = _factory.CreateClient();
        var response = await unauthClient.GetAsync("/api/v1/admin/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Auth_MaintainerHeader_AuthenticatesSuccessfully()
    {
        using var maintainerClient = _factory.CreateClient();
        maintainerClient.DefaultRequestHeaders.Add("X-Admin-Api-Key", TestAdminApiKey);

        var response = await maintainerClient.GetAsync("/api/v1/admin/generations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Auth_Logout_InvalidatesSession()
    {
        await AuthenticateAdminAsync();

        var logoutResp = await _client.PostAsync("/api/v1/admin/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logoutResp.StatusCode);

        var sessionResp = await _client.GetAsync("/api/v1/admin/auth/session");
        var body = await sessionResp.Content.ReadAsStringAsync();
        Assert.Contains("\"authenticated\":false", body);
    }

    [Fact]
    public async Task AccessKeys_SingleUseKey_ActivatesOnceAndConsumed()
    {
        await AuthenticateAdminAsync();

        // 1. Create single-use key
        var createResp = await _client.PostAsJsonAsync("/api/v1/admin/access-keys", new CreateAccessKeyRequest
        {
            Name = "Single Use Test Key",
            Type = AccessKeyType.SingleUse,
            Expiration = "30d"
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var plaintextKey = createJson.GetProperty("accessKey").GetString();
        Assert.NotNull(plaintextKey);
        Assert.StartsWith("DCSS-", plaintextKey);

        // 2. Client A enrolls with key
        var (_, pubKeyA) = ProfileCrypto.GenerateKeyPair();
        var enrollRespA = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest
        {
            EnrollmentTicket = plaintextKey,
            PublicKeyPem = pubKeyA
        });
        Assert.Equal(HttpStatusCode.OK, enrollRespA.StatusCode);

        // 3. Client B attempts to enroll with the SAME single-use key -> must fail
        var (_, pubKeyB) = ProfileCrypto.GenerateKeyPair();
        var enrollRespB = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest
        {
            EnrollmentTicket = plaintextKey,
            PublicKeyPem = pubKeyB
        });
        Assert.Equal(HttpStatusCode.Unauthorized, enrollRespB.StatusCode);
    }

    [Fact]
    public async Task AccessKeys_GroupKey_AuthorizesMultipleIndependentClients()
    {
        await AuthenticateAdminAsync();

        // 1. Create Group key with MaxUses = 5
        var createResp = await _client.PostAsJsonAsync("/api/v1/admin/access-keys", new CreateAccessKeyRequest
        {
            Name = "VIP Alpha Group",
            Type = AccessKeyType.Group,
            Expiration = "never",
            MaxUses = 5
        });
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var plaintextKey = createJson.GetProperty("accessKey").GetString()!;

        // 2. Enroll 3 independent clients
        var clientIds = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var (_, pubKey) = ProfileCrypto.GenerateKeyPair();
            var resp = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest
            {
                EnrollmentTicket = plaintextKey,
                PublicKeyPem = pubKey
            });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var enrollData = await resp.Content.ReadFromJsonAsync<JsonElement>();
            var cid = enrollData.GetProperty("clientId").GetString()!;
            clientIds.Add(cid);
        }

        // Verify distinct Client IDs
        Assert.Equal(3, clientIds.Distinct().Count());
    }

    [Fact]
    public async Task AccessKeys_GroupKey_EnforcesMaxUsesCapacity()
    {
        await AuthenticateAdminAsync();

        // Create Group key with MaxUses = 2
        var createResp = await _client.PostAsJsonAsync("/api/v1/admin/access-keys", new CreateAccessKeyRequest
        {
            Name = "Strict 2-User Group",
            Type = AccessKeyType.Group,
            Expiration = "7d",
            MaxUses = 2
        });
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var plaintextKey = createJson.GetProperty("accessKey").GetString()!;

        // 1st activation -> OK
        var (_, k1) = ProfileCrypto.GenerateKeyPair();
        var r1 = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest { EnrollmentTicket = plaintextKey, PublicKeyPem = k1 });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);

        // 2nd activation -> OK (reaches capacity)
        var (_, k2) = ProfileCrypto.GenerateKeyPair();
        var r2 = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest { EnrollmentTicket = plaintextKey, PublicKeyPem = k2 });
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        // 3rd activation -> Rejection (capacity exceeded)
        var (_, k3) = ProfileCrypto.GenerateKeyPair();
        var r3 = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest { EnrollmentTicket = plaintextKey, PublicKeyPem = k3 });
        Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);
    }

    [Fact]
    public async Task AccessKeys_RevokeKeyWithClients_RevokesKeyAndAssociatedClients()
    {
        await AuthenticateAdminAsync();

        // 1. Create Group key
        var createResp = await _client.PostAsJsonAsync("/api/v1/admin/access-keys", new CreateAccessKeyRequest
        {
            Name = "Revoke Target Group",
            Type = AccessKeyType.Group,
            Expiration = "never",
            MaxUses = 10
        });
        var createJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var keyId = createJson.GetProperty("record").GetProperty("id").GetString()!;
        var plaintextKey = createJson.GetProperty("accessKey").GetString()!;

        // 2. Enroll client
        var (_, pubKey) = ProfileCrypto.GenerateKeyPair();
        var enrollResp = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest
        {
            EnrollmentTicket = plaintextKey,
            PublicKeyPem = pubKey
        });
        var cid = (await enrollResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString()!;

        // 3. Revoke key with revokeClients = true
        var revokeResp = await _client.PostAsJsonAsync($"/api/v1/admin/access-keys/{keyId}/revoke", new RevokeKeyRequest
        {
            RevokeClients = true
        });
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        // 4. Verify client is revoked in clients list
        var clientResp = await _client.GetAsync($"/api/v1/admin/clients/{cid}");
        var clientData = await clientResp.Content.ReadFromJsonAsync<EnrolledClientRecord>();
        Assert.NotNull(clientData);
        Assert.False(clientData.IsActive);
    }

    [Fact]
    public async Task Clients_RestoreClient_ReactivatesAccess()
    {
        await AuthenticateAdminAsync();

        // Create key and enroll client
        var createResp = await _client.PostAsJsonAsync("/api/v1/admin/access-keys", new CreateAccessKeyRequest { Name = "Restore Test" });
        var plaintextKey = (await createResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessKey").GetString()!;
        var (_, pubKey) = ProfileCrypto.GenerateKeyPair();
        var enrollResp = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest { EnrollmentTicket = plaintextKey, PublicKeyPem = pubKey });
        var cid = (await enrollResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientId").GetString()!;

        // Revoke
        await _client.PostAsync($"/api/v1/admin/clients/{cid}/revoke", null);
        var r1 = await _client.GetFromJsonAsync<EnrolledClientRecord>($"/api/v1/admin/clients/{cid}");
        Assert.False(r1!.IsActive);

        // Restore
        await _client.PostAsync($"/api/v1/admin/clients/{cid}/restore", null);
        var r2 = await _client.GetFromJsonAsync<EnrolledClientRecord>($"/api/v1/admin/clients/{cid}");
        Assert.True(r2!.IsActive);
    }

    [Fact]
    public async Task Servers_UploadWireGuardConf_ParsesAndAddsWithoutExposingPrivateKey()
    {
        await AuthenticateAdminAsync();

        var conf = @"[Interface]
PrivateKey = aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=
Address = 10.2.0.2/32, fc00::2/128
DNS = 1.1.1.1, 8.8.8.8

[Peer]
PublicKey = c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=
Endpoint = 198.51.100.25:51820
AllowedIPs = 0.0.0.0/0, ::/0
PersistentKeepalive = 25";

        var addResp = await _client.PostAsJsonAsync("/api/v1/admin/servers", new AddServerRequest
        {
            DisplayName = "Brazil (Sao Paulo)",
            Country = "BR",
            Region = "Sao Paulo",
            ConfContent = conf
        });

        Assert.Equal(HttpStatusCode.OK, addResp.StatusCode);

        // Verify server is in list without private key
        var listResp = await _client.GetAsync("/api/v1/admin/servers");
        var listBody = await listResp.Content.ReadAsStringAsync();
        Assert.Contains("Brazil (Sao Paulo)", listBody);
        Assert.DoesNotContain("aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=", listBody);
    }

    [Fact]
    public async Task Generations_CreateAndPublishNewGeneration_IncrementsActiveGeneration()
    {
        await AuthenticateAdminAsync();

        var currentGenResp = await _client.GetFromJsonAsync<JsonElement>("/api/v1/health");
        var prevGen = currentGenResp.GetProperty("activeGeneration").GetInt32();

        var pubResp = await _client.PostAsync("/api/v1/admin/generations", null);
        Assert.Equal(HttpStatusCode.OK, pubResp.StatusCode);

        var newGenResp = await _client.GetFromJsonAsync<JsonElement>("/api/v1/health");
        var newGen = newGenResp.GetProperty("activeGeneration").GetInt32();

        Assert.True(newGen > prevGen);
    }

    [Fact]
    public async Task AuditLog_RecordsAdministrativeAndSecurityEvents()
    {
        await AuthenticateAdminAsync();

        var auditResp = await _client.GetAsync("/api/v1/admin/audit");
        Assert.Equal(HttpStatusCode.OK, auditResp.StatusCode);

        var events = await auditResp.Content.ReadFromJsonAsync<List<AuditEvent>>();
        Assert.NotNull(events);
        Assert.Contains(events, e => e.Action == "AdminLoginSucceeded");
    }

    [Fact]
    public async Task Dashboard_ReturnsAggregatedMetrics()
    {
        await AuthenticateAdminAsync();

        var dashResp = await _client.GetAsync("/api/v1/admin/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashResp.StatusCode);

        var dash = await dashResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dash.TryGetProperty("activeClientsCount", out _));
        Assert.True(dash.TryGetProperty("activeKeysCount", out _));
        Assert.True(dash.TryGetProperty("availableServersCount", out _));
    }
}
