using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.State;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;
using DCSS.Maintainer.ViewModels;
using DCSS.ProfileService.Services;
using Xunit;

namespace DC_ScreenSharing.IntegrationTests;

public class ProtonFullPipelineStandardizationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TestLogger _logger = new();

    private class TestLogger : IAppLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message, Exception? ex = null) { }
        public void Error(string message, Exception? ex = null) { }
        public IReadOnlyList<string> GetRecentLogs(int count = 50) => Array.Empty<string>();
    }

    public ProtonFullPipelineStandardizationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DCSS_ProtonPipe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void GenericWireGuardParser_ProtonProfile_ParsesAllFieldsAndComments()
    {
        var protonConf = """
            # Proton VPN WireGuard Configuration
            # Key for DCSS-Test
            # NetShield = 1
            # VPN Accelerator = on
            # US-FL#83
            ; Alternate endpoint:
            # Endpoint = [2a0d:5600:6:111::10]:51820

            [Interface]
            PrivateKey = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=
            Address = 10.2.0.2/32, 2a07:b944::2:2/128
            DNS = 10.2.0.1, 2a07:b944::2:1
            MTU = 1420

            [Peer]
            PublicKey = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = 37.221.112.210:51820
            PersistentKeepalive = 25
            """;

        var parsed = WireGuardConfParser.Parse(protonConf);
        Assert.NotNull(parsed);

        // 1. Interface
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=", parsed.PrivateKey);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Equal("10.2.0.2/32", parsed.Addresses[0]);
        Assert.Equal("2a07:b944::2:2/128", parsed.Addresses[1]);
        Assert.Equal(2, parsed.DnsServers.Count);
        Assert.Equal("10.2.0.1", parsed.DnsServers[0]);
        Assert.Equal("2a07:b944::2:1", parsed.DnsServers[1]);
        Assert.Equal(1420, parsed.Mtu);

        // 2. Peer
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=", parsed.PeerPublicKey);
        Assert.Equal(2, parsed.AllowedIpsList.Count);
        Assert.Equal("0.0.0.0/0", parsed.AllowedIpsList[0]);
        Assert.Equal("::/0", parsed.AllowedIpsList[1]);
        Assert.Equal("37.221.112.210", parsed.Endpoint);
        Assert.Equal(51820, parsed.Port);
        Assert.Equal(25, parsed.PersistentKeepalive);
    }

    [Fact]
    public void GenericWireGuardParser_EndpointFormats_IPv4_Hostname_IPv6()
    {
        // IPv4
        var confIpv4 = "[Interface]\nPrivateKey=a=\nAddress=10.2.0.2/32\n[Peer]\nPublicKey=b=\nEndpoint=37.221.112.210:51820";
        var parsedIpv4 = WireGuardConfParser.Parse(confIpv4);
        Assert.Equal("37.221.112.210", parsedIpv4.Endpoint);
        Assert.Equal(51820, parsedIpv4.Port);

        // Hostname
        var confHost = "[Interface]\nPrivateKey=a=\nAddress=10.2.0.2/32\n[Peer]\nPublicKey=b=\nEndpoint=example.protonvpn.net:51820";
        var parsedHost = WireGuardConfParser.Parse(confHost);
        Assert.Equal("example.protonvpn.net", parsedHost.Endpoint);
        Assert.Equal(51820, parsedHost.Port);

        // IPv6 in brackets
        var confIpv6 = "[Interface]\nPrivateKey=a=\nAddress=10.2.0.2/32\n[Peer]\nPublicKey=b=\nEndpoint=[2a0d:5600:6:111::10]:51820";
        var parsedIpv6 = WireGuardConfParser.Parse(confIpv6);
        Assert.Equal("2a0d:5600:6:111::10", parsedIpv6.Endpoint);
        Assert.Equal(51820, parsedIpv6.Port);
    }

    [Fact]
    public void GenericWireGuardParser_VPNBookProfile_ContinuesToWorkNatively()
    {
        var vpnbookConf = """
            [Interface]
            PrivateKey = oPPdF6dRTfRTqAyaCgM0ZiJW9riRBUzMPI0Xo+bXK0Y=
            Address = 10.104.9.144/32
            DNS = 1.1.1.1, 8.8.8.8

            [Peer]
            PublicKey = YLaLJahXZ6NuASXQLPl0eUPVAypirpaLuuO7tZa2bmo=
            Endpoint = 147.135.15.16:443
            AllowedIPs = 0.0.0.0/0, ::/0
            PersistentKeepalive = 25
            """;

        var parsed = WireGuardConfParser.Parse(vpnbookConf);
        Assert.NotNull(parsed);
        Assert.Single(parsed.Addresses);
        Assert.Equal("10.104.9.144/32", parsed.Addresses[0]);
        Assert.Equal(2, parsed.DnsServers.Count);
        Assert.Equal("1.1.1.1", parsed.DnsServers[0]);
        Assert.Equal("8.8.8.8", parsed.DnsServers[1]);
        Assert.Equal("147.135.15.16", parsed.Endpoint);
        Assert.Equal(443, parsed.Port);
    }

    [Fact]
    public void FullPipeline_Maintainer_ProfileService_Client_Cache_PreservesLists()
    {
        // 1. Key pair for signing
        var (privKey, pubKey) = ProfileCrypto.GenerateKeyPair();

        // 2. Maintainer imports Proton .conf
        var protonConf = """
            # Proton VPN WireGuard Profile
            [Interface]
            PrivateKey = aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=
            Address = 10.2.0.2/32, 2a07:b944::2:2/128
            DNS = 10.2.0.1, 2a07:b944::2:1
            MTU = 1420

            [Peer]
            PublicKey = bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb=
            AllowedIPs = 0.0.0.0/0, ::/0
            Endpoint = 37.221.112.210:51820
            PersistentKeepalive = 25
            """;

        var maintainerVm = new MaintainerViewModel();
        maintainerVm.PrivateKeyPem = privKey;
        maintainerVm.PublicKeyPem = pubKey;
        maintainerVm.Generation = 5;

        maintainerVm.Servers.Clear();
        var serverItem = new MaintainerServerItem
        {
            Id = "us-proton-01",
            Name = "Proton US FL#83",
            Region = "US",
            Enabled = true
        };
        maintainerVm.Servers.Add(serverItem);
        maintainerVm.ImportConfIntoServer(serverItem, protonConf);

        // Verify MaintainerServerItem has list fields populated
        Assert.Equal(2, serverItem.Addresses.Count);
        Assert.Equal("10.2.0.2/32", serverItem.Addresses[0]);
        Assert.Equal("2a07:b944::2:2/128", serverItem.Addresses[1]);
        Assert.Equal(2, serverItem.DnsServers.Count);
        Assert.Equal("10.2.0.1", serverItem.DnsServers[0]);
        Assert.Equal("2a07:b944::2:1", serverItem.DnsServers[1]);
        Assert.Equal(2, serverItem.AllowedIpsList.Count);
        Assert.Equal(25, serverItem.PersistentKeepalive);

        // 3. Build Signed Manifest
        var manifest = maintainerVm.BuildSignedManifest();
        Assert.NotNull(manifest);
        Assert.True(manifest.ProfilePayloads.ContainsKey("us-proton-01"));

        var profilePayloadJson = manifest.ProfilePayloads["us-proton-01"];
        Assert.NotNull(profilePayloadJson);

        // Verify JSON contains array for addresses, dnsServers, allowedIpsList
        using (var doc = JsonDocument.Parse(profilePayloadJson))
        {
            var wg = doc.RootElement.GetProperty("wireguard");
            var addrs = wg.GetProperty("addresses").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal(2, addrs.Count);
            Assert.Equal("10.2.0.2/32", addrs[0]);
            Assert.Equal("2a07:b944::2:2/128", addrs[1]);

            var dns = wg.GetProperty("dnsServers").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal(2, dns.Count);
            Assert.Equal("10.2.0.1", dns[0]);
            Assert.Equal("2a07:b944::2:1", dns[1]);

            var ips = wg.GetProperty("allowedIpsList").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Equal(2, ips.Count);
            Assert.Equal("0.0.0.0/0", ips[0]);
            Assert.Equal("::/0", ips[1]);
        }

        // 4. Ingest into ProfileService
        var profileStore = new ProfileStoreService(Path.Combine(_tempDir, "service_storage"), _logger);
        var publishedOk = profileStore.PublishGeneration(manifest, "Maintainer Test Upload");
        Assert.True(publishedOk);

        var retrievedProfile = profileStore.GetServerProfile("us-proton-01");
        Assert.NotNull(retrievedProfile);
        Assert.Equal(2, retrievedProfile.Wireguard.Addresses.Count);
        Assert.Equal("10.2.0.2/32", retrievedProfile.Wireguard.Addresses[0]);
        Assert.Equal("2a07:b944::2:2/128", retrievedProfile.Wireguard.Addresses[1]);
        Assert.Equal(2, retrievedProfile.Wireguard.DnsServers.Count);
        Assert.Equal(2, retrievedProfile.Wireguard.AllowedIpsList.Count);
        Assert.Equal(25, retrievedProfile.Wireguard.PersistentKeepalive);

        // 5. Client Secure Cache round-trip
        var clientCache = new SecureProfileStore(Path.Combine(_tempDir, "client_cache"), _logger);
        var cachedOk = clientCache.SaveProfile(retrievedProfile);
        Assert.True(cachedOk);

        var loadedProfile = clientCache.LoadProfile("us-proton-01");
        Assert.NotNull(loadedProfile);
        Assert.Equal(2, loadedProfile.Wireguard.Addresses.Count);
        Assert.Equal("10.2.0.2/32", loadedProfile.Wireguard.Addresses[0]);
        Assert.Equal("2a07:b944::2:2/128", loadedProfile.Wireguard.Addresses[1]);
        Assert.Equal(2, loadedProfile.Wireguard.DnsServers.Count);
        Assert.Equal("10.2.0.1", loadedProfile.Wireguard.DnsServers[0]);
        Assert.Equal("2a07:b944::2:1", loadedProfile.Wireguard.DnsServers[1]);
        Assert.Equal(2, loadedProfile.Wireguard.AllowedIpsList.Count);
        Assert.Equal(25, loadedProfile.Wireguard.PersistentKeepalive);

        // 6. Map to TunnelConfiguration and Validate with sing-box 1.13.19
        var tunnelConfig = new TunnelConfiguration
        {
            ServerId = loadedProfile.ServerId,
            ServerName = "Proton US FL#83",
            Endpoint = loadedProfile.Wireguard.Endpoint,
            Port = loadedProfile.Wireguard.Port,
            Addresses = new List<string>(loadedProfile.Wireguard.Addresses),
            DnsServers = new List<string>(loadedProfile.Wireguard.DnsServers),
            AllowedIpsList = new List<string>(loadedProfile.Wireguard.AllowedIpsList),
            PrivateKey = loadedProfile.Wireguard.PrivateKey,
            PeerPublicKey = loadedProfile.Wireguard.PeerPublicKey,
            PersistentKeepalive = loadedProfile.Wireguard.PersistentKeepalive,
            Mtu = loadedProfile.Wireguard.Mtu,
            DiscordExecutablePath = @"C:\Users\Test\AppData\Local\Discord\app-1.0.9254\Discord.exe"
        };

        var engine = new ProcessRoutingEngine(_logger, _tempDir);
        var engineConfigJson = engine.GenerateEngineConfig(tunnelConfig);
        Assert.NotNull(engineConfigJson);

        var (isSingboxValid, singboxError) = engine.ValidateRuntimeConfiguration(tunnelConfig);
        Assert.True(isSingboxValid, $"sing-box 1.13.19 check failed: {singboxError}");

        // 7. Full State Machine Lifecycle (Connect -> Disconnect -> Reconnect)
        var stateMachine = new ConnectionStateMachine(_logger);
        Assert.Equal(ConnectionState.Disconnected, stateMachine.CurrentState);

        // Connect sequence
        Assert.True(stateMachine.TransitionTo(ConnectionState.Preparing, "Preparing profile"));
        Assert.True(stateMachine.TransitionTo(ConnectionState.StartingTunnel, "Starting WireGuard tunnel"));
        Assert.True(stateMachine.TransitionTo(ConnectionState.Connecting, "Routing processes"));
        Assert.True(stateMachine.TransitionTo(ConnectionState.Connected, "Connected"));
        Assert.True(stateMachine.IsConnected);

        // Disconnect
        Assert.True(stateMachine.TransitionTo(ConnectionState.Disconnecting, "Stopping tunnel"));
        Assert.True(stateMachine.TransitionTo(ConnectionState.Disconnected, "Disconnected"));
        Assert.False(stateMachine.IsConnected);

        // Reconnect
        Assert.True(stateMachine.TransitionTo(ConnectionState.Preparing, "Preparing reconnect"));
        Assert.True(stateMachine.TransitionTo(ConnectionState.StartingTunnel, "Starting WireGuard tunnel"));
        Assert.True(stateMachine.TransitionTo(ConnectionState.Connecting, "Routing processes"));
        Assert.True(stateMachine.TransitionTo(ConnectionState.Connected, "Connected"));
        Assert.True(stateMachine.IsConnected);
    }
}
