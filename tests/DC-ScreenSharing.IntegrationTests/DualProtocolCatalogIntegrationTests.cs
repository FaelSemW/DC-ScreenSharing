using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DCScreenSharing.Core.Profiles;
using DCSS.ProfileService.Controllers;
using DCSS.ProfileService.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DCScreenSharing.IntegrationTests;

public class DualProtocolCatalogIntegrationTests : IClassFixture<WebApplicationFactory<global::Program>>
{
    private readonly WebApplicationFactory<global::Program> _factory;
    private readonly HttpClient _client;

    private const string TestAdminApiKey = "test-admin-secret-key-12345";
    private readonly string _testStorageDir;

    public DualProtocolCatalogIntegrationTests(WebApplicationFactory<global::Program> factory)
    {
        _testStorageDir = Path.Combine(Path.GetTempPath(), "DCSS_DualProtoTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testStorageDir);
        Environment.SetEnvironmentVariable("ProfileService__StoragePath", _testStorageDir);
        Environment.SetEnvironmentVariable("ADMIN_API_KEY", TestAdminApiKey);

        _factory = factory.WithWebHostBuilder(builder => { });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    private async Task AuthenticateAdminAsync()
    {
        var loginResp = await _client.PostAsJsonAsync("/api/v1/admin/auth/login", new AdminLoginRequest
        {
            ApiKey = TestAdminApiKey
        });
        Assert.True(loginResp.IsSuccessStatusCode, "Admin login failed during test setup.");
    }

    [Fact]
    public async Task OpenVpn_ValidationEndpoint_ReturnsSafeMetadata()
    {
        await AuthenticateAdminAsync();

        var ovpn = @"
client
dev tun
proto udp
remote 185.159.157.1 1194
remote 185.159.157.1 4569
resolv-retry infinite
nobind
persist-key
persist-tun
cipher AES-256-GCM
auth SHA512
auth-user-pass
<ca>
-----BEGIN CERTIFICATE-----
MIIProtonCA...
-----END CERTIFICATE-----
</ca>
";

        var resp = await _client.PostAsJsonAsync("/api/v1/admin/openvpn/validate", new ValidateOpenVpnRequest
        {
            OvpnContent = ovpn,
            Provider = "Proton"
        });

        Assert.True(resp.IsSuccessStatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("isValid").GetBoolean());
        Assert.Equal("UDP", root.GetProperty("protocol").GetString());
        Assert.Equal("185.159.157.1:1194", root.GetProperty("primaryRemote").GetString());
        Assert.Equal(1, root.GetProperty("additionalRemotesCount").GetInt32());
        Assert.Equal("Username/Password", root.GetProperty("authType").GetString());
        Assert.Equal("Proton", root.GetProperty("provider").GetString());
    }

    [Fact]
    public async Task OpenVpn_MaliciousDirectives_AreRejectedByValidationAndAddEndpoints()
    {
        await AuthenticateAdminAsync();

        var evilOvpn = @"
client
dev tun
proto udp
remote 1.2.3.4 1194
script-security 2
up evil.bat
<ca>
CA
</ca>
";

        var valResp = await _client.PostAsJsonAsync("/api/v1/admin/openvpn/validate", new ValidateOpenVpnRequest
        {
            OvpnContent = evilOvpn
        });

        var valJson = await valResp.Content.ReadAsStringAsync();
        using var valDoc = JsonDocument.Parse(valJson);
        Assert.False(valDoc.RootElement.GetProperty("isValid").GetBoolean());

        var addResp = await _client.PostAsJsonAsync("/api/v1/admin/servers/openvpn", new AddOpenVpnServerRequest
        {
            DisplayName = "Malicious Server",
            Country = "US",
            OvpnContent = evilOvpn
        });

        Assert.Equal(HttpStatusCode.BadRequest, addResp.StatusCode);
    }

    [Fact]
    public async Task CredentialSet_LifecycleAndRotation_Succeeds()
    {
        await AuthenticateAdminAsync();

        // 1. Create Credential Set
        var createResp = await _client.PostAsJsonAsync("/api/v1/admin/openvpn/credential-sets", new CreateCredentialSetRequest
        {
            Name = "VPNBook Rotation Test Set",
            Provider = "VPNBook",
            Username = "vpnbook",
            Password = "InitialPassword123"
        });

        Assert.True(createResp.IsSuccessStatusCode);
        var createJson = await createResp.Content.ReadAsStringAsync();
        using var createDoc = JsonDocument.Parse(createJson);
        var credSetId = createDoc.RootElement.GetProperty("credentialSet").GetProperty("id").GetString()!;

        // Verify password is NOT in GET response
        var listResp = await _client.GetAsync("/api/v1/admin/openvpn/credential-sets");
        var listJson = await listResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("InitialPassword123", listJson);

        // 2. Rotate Password
        var updateResp = await _client.PutAsJsonAsync($"/api/v1/admin/openvpn/credential-sets/{credSetId}", new UpdateCredentialSetRequest
        {
            Name = "VPNBook Rotation Test Set",
            Password = "RotatedNewPassword456"
        });
        Assert.True(updateResp.IsSuccessStatusCode);

        // Verify new password is also NOT exposed in GET
        var listResp2 = await _client.GetAsync("/api/v1/admin/openvpn/credential-sets");
        var listJson2 = await listResp2.Content.ReadAsStringAsync();
        Assert.DoesNotContain("RotatedNewPassword456", listJson2);
    }

    [Fact]
    public async Task DualProtocol_PublishMixedGeneration_AndCapabilityFilter()
    {
        await AuthenticateAdminAsync();

        // 1. Add OpenVPN server linked to Credential Set
        var credResp = await _client.PostAsJsonAsync("/api/v1/admin/openvpn/credential-sets", new CreateCredentialSetRequest
        {
            Name = "Proton Mixed Gen Set",
            Provider = "Proton",
            Username = "protonuser123",
            Password = "ProtonSecretPassword!"
        });
        var credJson = await credResp.Content.ReadAsStringAsync();
        using var credDoc = JsonDocument.Parse(credJson);
        var credId = credDoc.RootElement.GetProperty("credentialSet").GetProperty("id").GetString();

        var protonOvpn = @"
client
dev tun
proto udp
remote 185.159.157.1 1194
resolv-retry infinite
nobind
persist-key
persist-tun
cipher AES-256-GCM
auth SHA512
auth-user-pass
<ca>
-----BEGIN CERTIFICATE-----
MIIProtonCA...
-----END CERTIFICATE-----
</ca>
";

        var addOvpnResp = await _client.PostAsJsonAsync("/api/v1/admin/servers/openvpn", new AddOpenVpnServerRequest
        {
            DisplayName = "Argentina OpenVPN",
            Country = "Argentina",
            CountryCode = "AR",
            Region = "South America",
            City = "Buenos Aires",
            Provider = "Proton",
            OvpnContent = protonOvpn,
            CredentialSetId = credId
        });
        Assert.True(addOvpnResp.IsSuccessStatusCode);

        // 2. Publish Generation
        var pubResp = await _client.PostAsync("/api/v1/admin/generations", null);
        var pubErr = await pubResp.Content.ReadAsStringAsync();
        Assert.True(pubResp.IsSuccessStatusCode, $"Publish failed with status {pubResp.StatusCode}: {pubErr}");

        // 3. Test Legacy Client without capability header -> WireGuard ONLY
        var legacyReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog");
        var legacyResp = await _client.SendAsync(legacyReq);
        Assert.True(legacyResp.IsSuccessStatusCode);
        var legacyCatalog = await legacyResp.Content.ReadFromJsonAsync<ServerCatalog>();
        Assert.NotNull(legacyCatalog);
        Assert.All(legacyCatalog.Servers, s => Assert.Equal(VpnProtocol.WireGuard, s.Protocol));
        Assert.DoesNotContain(legacyCatalog.Servers, s => s.Protocol == VpnProtocol.OpenVpn);

        // 4. Test Dual-Protocol Client with capability header -> WireGuard + OpenVPN
        var dualReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog");
        dualReq.Headers.Add("X-Client-Capabilities", "wireguard-v1,openvpn-v1");
        var dualResp = await _client.SendAsync(dualReq);
        Assert.True(dualResp.IsSuccessStatusCode);
        var dualCatalog = await dualResp.Content.ReadFromJsonAsync<ServerCatalog>();
        Assert.NotNull(dualCatalog);
        Assert.Contains(dualCatalog.Servers, s => s.Protocol == VpnProtocol.WireGuard);
        Assert.Contains(dualCatalog.Servers, s => s.Protocol == VpnProtocol.OpenVpn);

        // 5. Test Zero Secrets Exposure in public catalog
        var catalogRaw = await dualResp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ProtonSecretPassword!", catalogRaw);
        Assert.DoesNotContain("aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=", catalogRaw);
    }

    [Fact]
    public async Task PublicationStatus_TracksPendingChangesAccurately()
    {
        await AuthenticateAdminAsync();

        // 1. Check publication status endpoint
        var statusResp = await _client.GetAsync("/api/v1/admin/servers/publication-status");
        Assert.True(statusResp.IsSuccessStatusCode);
        var status = await statusResp.Content.ReadFromJsonAsync<PublicationStatusSummary>();
        Assert.NotNull(status);

        // 2. Add an OpenVPN server without publishing
        var ovpnContent = @"
client
dev tun
proto udp
remote 198.51.100.1 1194
resolv-retry infinite
nobind
persist-key
persist-tun
cipher AES-256-GCM
auth SHA256
<ca>
-----BEGIN CERTIFICATE-----
MIIFakeCert...
-----END CERTIFICATE-----
</ca>
";
        var addResp = await _client.PostAsJsonAsync("/api/v1/admin/servers/openvpn", new AddOpenVpnServerRequest
        {
            DisplayName = "Test Unpublished OVPN",
            Country = "Germany",
            CountryCode = "DE",
            Region = "Europe",
            Provider = "VPNBook",
            OvpnContent = ovpnContent,
            PublishImmediately = false
        });
        Assert.True(addResp.IsSuccessStatusCode);

        // 3. Status should indicate pending changes
        var updatedStatusResp = await _client.GetAsync("/api/v1/admin/servers/publication-status");
        var updatedStatus = await updatedStatusResp.Content.ReadFromJsonAsync<PublicationStatusSummary>();
        Assert.NotNull(updatedStatus);
        Assert.True(updatedStatus.HasPendingChanges);
        Assert.True(updatedStatus.PendingAdditionsCount > 0);

        // 4. Verify GetServers decorates item with publication status
        var serversResp = await _client.GetAsync("/api/v1/admin/servers");
        var servers = await serversResp.Content.ReadFromJsonAsync<List<ServerRegistryItem>>();
        Assert.NotNull(servers);
        var unpubServer = servers.FirstOrDefault(s => s.Name == "Test Unpublished OVPN");
        Assert.NotNull(unpubServer);
        Assert.Equal("NOT_PUBLISHED", unpubServer.PublicationStatus);
    }

    [Fact]
    public async Task AddServer_WithPublishImmediately_PublishesNewGenerationAtomically()
    {
        await AuthenticateAdminAsync();

        var ovpnContent = @"
client
dev tun
proto udp
remote 203.0.113.5 1194
resolv-retry infinite
nobind
persist-key
persist-tun
cipher AES-256-GCM
auth SHA256
<ca>
-----BEGIN CERTIFICATE-----
MIIFakeCert...
-----END CERTIFICATE-----
</ca>
";
        var addResp = await _client.PostAsJsonAsync("/api/v1/admin/servers/openvpn", new AddOpenVpnServerRequest
        {
            DisplayName = "Atomic Publish Server",
            Country = "Canada",
            CountryCode = "CA",
            Region = "North America",
            Provider = "Proton",
            OvpnContent = ovpnContent,
            PublishImmediately = true
        });
        Assert.True(addResp.IsSuccessStatusCode);

        // Verify catalog immediately contains this server
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog");
        req.Headers.Add("X-Client-Capabilities", "wireguard-v1,openvpn-v1");
        var catalogResp = await _client.SendAsync(req);
        Assert.True(catalogResp.IsSuccessStatusCode);
        var catalog = await catalogResp.Content.ReadFromJsonAsync<ServerCatalog>();
        Assert.NotNull(catalog);
        Assert.Contains(catalog.Servers, s => s.Name == "Atomic Publish Server");
    }
}
