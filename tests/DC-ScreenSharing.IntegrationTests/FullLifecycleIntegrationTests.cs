using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.Security;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;
using DCSS.Maintainer.ViewModels;
using DCSS.ProfileService.Controllers;
using DCSS.ProfileService.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DC_ScreenSharing.IntegrationTests;

public class FullLifecycleIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly string _testStorageDir;

    public FullLifecycleIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _testStorageDir = Path.Combine(Path.GetTempPath(), "DCSS_ProfileService_Test_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ProfileService__StoragePath", _testStorageDir);
        Environment.SetEnvironmentVariable("ADMIN_API_KEY", "test-admin-secret-key-12345");

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
            });
        });

        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", body);
        Assert.Contains("DCSS.ProfileService", body);
    }

    [Fact]
    public async Task InitialCatalog_ContainsDefaultServers()
    {
        var response = await _client.GetAsync("/api/v1/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var catalog = await response.Content.ReadFromJsonAsync<ServerCatalog>();
        Assert.NotNull(catalog);
        Assert.True(catalog.Generation >= 1);
        Assert.Contains(catalog.Servers, s => s.Id == "us-01");
        Assert.Contains(catalog.Servers, s => s.Id == "de-01");
    }

    [Fact]
    public async Task AdminPublish_WithoutApiKey_Returns401Unauthorized()
    {
        var manifest = new SignedManifest { Generation = 99 };
        var response = await _client.PostAsJsonAsync("/api/v1/admin/publish", manifest);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminPublish_WithWrongApiKey_Returns403Forbidden()
    {
        var manifest = new SignedManifest { Generation = 99 };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/publish")
        {
            Content = JsonContent.Create(manifest)
        };
        request.Headers.Add("X-Admin-Api-Key", "wrong-invalid-key");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Enrollment_WithoutTicket_Rejected()
    {
        var req = new EnrollClientRequest
        {
            EnrollmentTicket = "",
            PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE\n-----END PUBLIC KEY-----"
        };
        var resp = await _client.PostAsJsonAsync("/api/v1/client/enroll", req);
        Assert.True(resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Enrollment_WithInvalidTicket_Rejected()
    {
        var req = new EnrollClientRequest
        {
            EnrollmentTicket = "DCSS-ENROLL-INVALID-00000000",
            PublicKeyPem = "-----BEGIN PUBLIC KEY-----\nMFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE\n-----END PUBLIC KEY-----"
        };
        var resp = await _client.PostAsJsonAsync("/api/v1/client/enroll", req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Enrollment_WithReusedTicket_Rejected()
    {
        // 1. Admin creates ticket
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/tickets")
        {
            Content = JsonContent.Create(new CreateTicketRequest { ValidityMinutes = 30 })
        };
        createReq.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var createResp = await _client.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);
        var ticketJson = await createResp.Content.ReadFromJsonAsync<TicketResponse>();
        Assert.NotNull(ticketJson);

        // 2. Client 1 enrolls with ticket -> Success
        var (_, pubKey1) = ProfileCrypto.GenerateKeyPair();
        var enroll1 = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest
        {
            EnrollmentTicket = ticketJson.Ticket,
            PublicKeyPem = pubKey1
        });
        Assert.Equal(HttpStatusCode.OK, enroll1.StatusCode);

        // 3. Client 2 tries to reuse the same ticket -> Rejected (401 Unauthorized)
        var (_, pubKey2) = ProfileCrypto.GenerateKeyPair();
        var enroll2 = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest
        {
            EnrollmentTicket = ticketJson.Ticket,
            PublicKeyPem = pubKey2
        });
        Assert.Equal(HttpStatusCode.Unauthorized, enroll2.StatusCode);
    }

    [Fact]
    public async Task Enrollment_WithRevokedTicket_Rejected()
    {
        // 1. Admin creates ticket
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/tickets")
        {
            Content = JsonContent.Create(new CreateTicketRequest { ValidityMinutes = 30 })
        };
        createReq.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var createResp = await _client.SendAsync(createReq);
        var ticketJson = await createResp.Content.ReadFromJsonAsync<TicketResponse>();
        Assert.NotNull(ticketJson);

        // 2. Admin revokes ticket
        using var revokeReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/tickets/{ticketJson.TicketHash}/revoke");
        revokeReq.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var revokeResp = await _client.SendAsync(revokeReq);
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        // 3. Client attempts to enroll with revoked ticket -> Rejected
        var (_, pubKey) = ProfileCrypto.GenerateKeyPair();
        var enroll = await _client.PostAsJsonAsync("/api/v1/client/enroll", new EnrollClientRequest
        {
            EnrollmentTicket = ticketJson.Ticket,
            PublicKeyPem = pubKey
        });
        Assert.Equal(HttpStatusCode.Unauthorized, enroll.StatusCode);
    }

    [Fact]
    public async Task RevokedClient_ProofOfPossession_Rejected()
    {
        // 1. Admin creates ticket
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/tickets")
        {
            Content = JsonContent.Create(new CreateTicketRequest { ValidityMinutes = 30 })
        };
        createReq.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var createResp = await _client.SendAsync(createReq);
        var ticketJson = await createResp.Content.ReadFromJsonAsync<TicketResponse>();
        Assert.NotNull(ticketJson);

        // 2. Client enrolls
        var clientIdentity = new ClientIdentity(Path.Combine(Path.GetTempPath(), "test_id_rev_" + Guid.NewGuid().ToString("N")), new FileLogger(Path.GetTempPath()));
        var coordinator = new ProfileRotationCoordinator(
            new SecureProfileStore(Path.Combine(Path.GetTempPath(), "client_store_rev_" + Guid.NewGuid().ToString("N")), new FileLogger(Path.GetTempPath())),
            new FileLogger(Path.GetTempPath()),
            null,
            "http://localhost",
            _client,
            clientIdentity);

        var (enrolled, _) = await coordinator.EnrollWithTicketAsync(ticketJson.Ticket);
        Assert.True(enrolled);

        // 3. Admin revokes ClientId
        using var revokeClientReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/clients/{clientIdentity.ClientId}/revoke");
        revokeClientReq.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var revokeResp = await _client.SendAsync(revokeClientReq);
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        // 4. Revoked client tries to acquire profile -> Rejected
        var profile = await coordinator.TryFetchProfileAsync("us-01");
        Assert.Null(profile);
    }

    [Fact]
    public async Task EndToEndUserLifecycle_FreshInstall_Activation_Relaunch_Revocation_Reactivation()
    {
        // --- PHASE 1: Fresh Install State (No ClientId enrolled) ---
        var identityStorageFile = Path.Combine(Path.GetTempPath(), "test_lifecycle_id_" + Guid.NewGuid().ToString("N"));
        var clientStoreDir = Path.Combine(Path.GetTempPath(), "test_lifecycle_store_" + Guid.NewGuid().ToString("N"));
        var logger = new FileLogger(Path.GetTempPath());

        var clientIdentity = new ClientIdentity(identityStorageFile, logger);
        Assert.False(clientIdentity.IsEnrolled, "Fresh install must not be enrolled.");
        Assert.Empty(clientIdentity.ClientId);

        // Coordinator instantiated on fresh install
        var coordinator = new ProfileRotationCoordinator(
            new SecureProfileStore(clientStoreDir, logger),
            logger,
            null,
            "http://localhost",
            _client,
            clientIdentity);

        // Profile request fails because activation is required
        var initialProfileAttempt = await coordinator.TryFetchProfileAsync("us-01");
        Assert.Null(initialProfileAttempt);

        // --- PHASE 2: Maintainer generates Activation Code ---
        using var createTicketReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/tickets")
        {
            Content = JsonContent.Create(new CreateTicketRequest { ValidityMinutes = 30, Description = "E2E Test User" })
        };
        createTicketReq.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var createTicketResp = await _client.SendAsync(createTicketReq);
        Assert.Equal(HttpStatusCode.OK, createTicketResp.StatusCode);
        var ticketData = await createTicketResp.Content.ReadFromJsonAsync<TicketResponse>();
        Assert.NotNull(ticketData);
        var activationCode = ticketData.Ticket;
        Assert.StartsWith("DCSS-ENROLL-", activationCode);

        // --- PHASE 3: User activates client with Activation Code ---
        var (activateSuccess, activateMsg) = await coordinator.EnrollWithTicketAsync(activationCode);
        Assert.True(activateSuccess, activateMsg);
        Assert.True(clientIdentity.IsEnrolled, "Client must be enrolled after activation.");
        Assert.NotEmpty(clientIdentity.ClientId);

        // Catalog loads and profile acquisition succeeds
        var catalog = await coordinator.FetchRemoteCatalogAsync();
        Assert.NotNull(catalog);
        var activeProfile = await coordinator.TryFetchProfileAsync("us-01");
        Assert.NotNull(activeProfile);
        Assert.Equal("us-01", activeProfile.ServerId);

        // --- PHASE 4: Client Relaunch Persistence ---
        // Simulate app closing and reopening by re-instantiating ClientIdentity from same storage path
        var relaunchedIdentity = new ClientIdentity(identityStorageFile, logger);
        Assert.True(relaunchedIdentity.IsEnrolled, "Relaunched client must remember activation state.");
        Assert.Equal(clientIdentity.ClientId, relaunchedIdentity.ClientId);

        var relaunchedCoordinator = new ProfileRotationCoordinator(
            new SecureProfileStore(clientStoreDir, logger),
            logger,
            null,
            "http://localhost",
            _client,
            relaunchedIdentity);

        // Profile acquisition works immediately without re-entering activation code
        var relaunchedProfile = await relaunchedCoordinator.TryFetchProfileAsync("us-01");
        Assert.NotNull(relaunchedProfile);

        // --- PHASE 5: Admin Revokes ClientId ---
        using var revokeReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/clients/{relaunchedIdentity.ClientId}/revoke");
        revokeReq.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var revokeResp = await _client.SendAsync(revokeReq);
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        // Profile request now fails
        var revokedProfileAttempt = await relaunchedCoordinator.TryFetchProfileAsync("us-01");
        Assert.Null(revokedProfileAttempt);

        // Reset enrolled ClientId (simulating UI transition to activation required)
        relaunchedIdentity.SetEnrolledClientId(string.Empty);
        Assert.False(relaunchedIdentity.IsEnrolled);

        // --- PHASE 6: Reactivation with New Activation Code ---
        using var createTicket2Req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/tickets")
        {
            Content = JsonContent.Create(new CreateTicketRequest { ValidityMinutes = 30 })
        };
        createTicket2Req.Headers.Add("X-Admin-Api-Key", "test-admin-secret-key-12345");
        var createTicket2Resp = await _client.SendAsync(createTicket2Req);
        var ticket2Data = await createTicket2Resp.Content.ReadFromJsonAsync<TicketResponse>();
        Assert.NotNull(ticket2Data);

        var (reactivateSuccess, _) = await relaunchedCoordinator.EnrollWithTicketAsync(ticket2Data.Ticket);
        Assert.True(reactivateSuccess);
        Assert.True(relaunchedIdentity.IsEnrolled);

        // Profile acquisition restored
        var restoredProfile = await relaunchedCoordinator.TryFetchProfileAsync("us-01");
        Assert.NotNull(restoredProfile);
        Assert.Equal("us-01", restoredProfile.ServerId);
    }

    [Fact]
    public async Task LiveProductionEndpoint_Publish_And_ClientCatalog_Verification()
    {
        var configsDir = @"d:\DC-ScreenSharing\configs";
        if (!Directory.Exists(configsDir)) return;

        var adminApiKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY");
        if (string.IsNullOrWhiteSpace(adminApiKey) || adminApiKey.Contains("replace") || adminApiKey == "test-admin-secret-key-12345")
        {
            return;
        }

        var usConf = Path.Combine(configsDir, "us.conf");
        var caConf = Path.Combine(configsDir, "ca.conf");
        var ukConf = Path.Combine(configsDir, "uk.conf");

        if (!File.Exists(usConf) || !File.Exists(caConf) || !File.Exists(ukConf)) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var healthResp = await http.GetAsync("https://zaprecovery.online/api/v1/health");
        if (!healthResp.IsSuccessStatusCode) return;

        var healthJson = await healthResp.Content.ReadFromJsonAsync<JsonElement>();
        var currentGen = healthJson.GetProperty("activeGeneration").GetInt32();

        // 1. Maintainer imports real local configs
        var maintainerVm = new MaintainerViewModel
        {
            Generation = currentGen + 1,
            ServiceUrl = "https://zaprecovery.online",
            AdminApiKey = adminApiKey
        };

        var usServer = new MaintainerServerItem { Id = "us-01", Name = "United States (East)", Region = "US" };
        maintainerVm.ImportConfIntoServer(usServer, File.ReadAllText(usConf));
        maintainerVm.Servers.Add(usServer);

        var caServer = new MaintainerServerItem { Id = "ca-01", Name = "Canada (Toronto)", Region = "CA" };
        maintainerVm.ImportConfIntoServer(caServer, File.ReadAllText(caConf));
        maintainerVm.Servers.Add(caServer);

        var ukServer = new MaintainerServerItem { Id = "uk-01", Name = "United Kingdom (London)", Region = "UK" };
        maintainerVm.ImportConfIntoServer(ukServer, File.ReadAllText(ukConf));
        maintainerVm.Servers.Add(ukServer);

        // 2. Publish Generation to production
        var (publishSuccess, publishMsg) = await maintainerVm.PublishToServiceAsync();
        Assert.True(publishSuccess, publishMsg);

        // 3. Verify public catalog on https://zaprecovery.online/api/v1/catalog
        var catalog = await http.GetFromJsonAsync<ServerCatalog>("https://zaprecovery.online/api/v1/catalog");
        Assert.NotNull(catalog);
        Assert.Equal(currentGen + 1, catalog.Generation);
        Assert.Contains(catalog.Servers, s => s.Id == "us-01" && s.Name == "United States (East)");
        Assert.Contains(catalog.Servers, s => s.Id == "ca-01" && s.Name == "Canada (Toronto)");
        Assert.Contains(catalog.Servers, s => s.Id == "uk-01" && s.Name == "United Kingdom (London)");

        // 4. Generate production activation code
        var (ticketOk, ticket, ticketMsg) = await maintainerVm.GenerateEnrollmentTicketAsync(30, "Live E2E Verification");
        Assert.True(ticketOk, ticketMsg);

        // 5. Client activates and acquires profile over HTTPS
        var clientIdentity = new ClientIdentity(Path.Combine(Path.GetTempPath(), "test_live_prod_" + Guid.NewGuid().ToString("N")), new FileLogger(Path.GetTempPath()));
        var coordinator = new ProfileRotationCoordinator(
            new SecureProfileStore(Path.Combine(Path.GetTempPath(), "client_store_live_prod_" + Guid.NewGuid().ToString("N")), new FileLogger(Path.GetTempPath())),
            new FileLogger(Path.GetTempPath()),
            maintainerVm.PublicKeyPem,
            "https://zaprecovery.online",
            http,
            clientIdentity);

        var (activated, actMsg) = await coordinator.EnrollWithTicketAsync(ticket);
        Assert.True(activated, actMsg);

        var profile = await coordinator.TryFetchProfileAsync("us-01");
        Assert.NotNull(profile);
        Assert.Equal("us-01", profile.ServerId);
        Assert.False(string.IsNullOrEmpty(profile.Wireguard.PrivateKey));
        Assert.False(string.IsNullOrEmpty(profile.Wireguard.PeerPublicKey));
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
}
