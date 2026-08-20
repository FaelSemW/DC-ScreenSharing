using System.IO;
using System.Text.Json;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.State;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;
using Xunit;

namespace DC_ScreenSharing.IntegrationTests;

public class ProtonProfileLifecycleTests
{
    private class TestLogger : IAppLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message, Exception? ex = null) { }
        public void Error(string message, Exception? ex = null) { }
        public IReadOnlyList<string> GetRecentLogs(int count = 50) => Array.Empty<string>();
    }

    [Fact]
    public void RealProtonProfile_FullLifecycle_PassesAllStages()
    {
        var logger = new TestLogger();
        var tempDir = Path.Combine(Path.GetTempPath(), "DCSS_Proton_Lifecycle_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Real Proton WireGuard .conf content structure
            var protonConf = @"
# ProtonVPN WireGuard Configuration
# Country: United States
# Server: US-FREE#123456

[Interface]
# Client Private Key
PrivateKey = oPPdF6dRTfRTqAyaCgM0ZiJW9riRBUzMPI0Xo+bXK0Y=

# Dual-stack assigned interface addresses
Address = 10.2.0.2/32, 2a07:b944::2:2/128

# Dual-stack DNS resolvers
DNS = 10.2.0.1, 2a07:b944::2:1

[Peer]
# Server Public Key
PublicKey = YLaLJahXZ6NuASXQLPl0eUPVAypirpaLuuO7tZa2bmo=

# Dual-stack allowed IPs
AllowedIPs = 0.0.0.0/0, ::/0

# Proton server endpoint
Endpoint = 185.159.158.1:51820

# Keepalive for NAT traversal
PersistentKeepalive = 25
";

            // Stage 1: Parse -> PASS
            var parsed = WireGuardConfParser.Parse(protonConf);
            Assert.NotNull(parsed);
            Assert.Equal("oPPdF6dRTfRTqAyaCgM0ZiJW9riRBUzMPI0Xo+bXK0Y=", parsed.PrivateKey);
            Assert.Equal("YLaLJahXZ6NuASXQLPl0eUPVAypirpaLuuO7tZa2bmo=", parsed.PeerPublicKey);
            Assert.Equal("185.159.158.1", parsed.Endpoint);
            Assert.Equal(51820, parsed.Port);
            Assert.Equal(25, parsed.PersistentKeepalive);

            // Multi-value list parsing checks
            Assert.Equal(2, parsed.Addresses.Count);
            Assert.Equal("10.2.0.2/32", parsed.Addresses[0]);
            Assert.Equal("2a07:b944::2:2/128", parsed.Addresses[1]);

            Assert.Equal(2, parsed.DnsServers.Count);
            Assert.Equal("10.2.0.1", parsed.DnsServers[0]);
            Assert.Equal("2a07:b944::2:1", parsed.DnsServers[1]);

            Assert.Equal(2, parsed.AllowedIpsList.Count);
            Assert.Equal("0.0.0.0/0", parsed.AllowedIpsList[0]);
            Assert.Equal("::/0", parsed.AllowedIpsList[1]);

            // Stage 2: Config generation -> PASS
            var engine = new ProcessRoutingEngine(logger, tempDir);
            var tunnelConfig = new TunnelConfiguration
            {
                ServerId = "us-free-01",
                ServerName = "US Free 01",
                Endpoint = parsed.Endpoint,
                Port = parsed.Port,
                Addresses = new List<string>(parsed.Addresses),
                DnsServers = new List<string>(parsed.DnsServers),
                AllowedIpsList = new List<string>(parsed.AllowedIpsList),
                PrivateKey = parsed.PrivateKey,
                PeerPublicKey = parsed.PeerPublicKey,
                PersistentKeepalive = parsed.PersistentKeepalive,
                DiscordExecutablePath = @"C:\Users\TestUser\AppData\Local\Discord\app-1.0.9254\Discord.exe"
            };

            var generatedJson = engine.GenerateEngineConfig(tunnelConfig);
            Assert.NotNull(generatedJson);

            using var doc = JsonDocument.Parse(generatedJson);
            var root = doc.RootElement;

            var endpoints = root.GetProperty("endpoints");
            var addresses = endpoints[0].GetProperty("address").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("10.2.0.2/32", addresses);
            Assert.Contains("2a07:b944::2:2/128", addresses);

            var peer = endpoints[0].GetProperty("peers")[0];
            var allowedIps = peer.GetProperty("allowed_ips").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("0.0.0.0/0", allowedIps);
            Assert.Contains("::/0", allowedIps);
            Assert.Equal(25, peer.GetProperty("persistent_keepalive_interval").GetInt32());

            // Stage 3: sing-box validation (exit code 0) -> PASS
            var (isValid, error) = engine.ValidateRuntimeConfiguration(tunnelConfig);
            Assert.True(isValid, $"sing-box validation error: {error}");
            Assert.Null(error);

            // Stage 4: StartTunnel -> PASS
            var stateMachine = new ConnectionStateMachine(logger);
            Assert.Equal(ConnectionState.Disconnected, stateMachine.CurrentState);

            var prepOk = stateMachine.TransitionTo(ConnectionState.Preparing, "Preparing Proton tunnel");
            Assert.True(prepOk);

            var startOk = stateMachine.TransitionTo(ConnectionState.StartingTunnel, "Starting Proton WireGuard tunnel");
            Assert.True(startOk);

            var connOk = stateMachine.TransitionTo(ConnectionState.Connecting, "Routing processes to tunnel");
            Assert.True(connOk);

            // Stage 5: Connected -> PASS
            var connectedOk = stateMachine.TransitionTo(ConnectionState.Connected, "Connected to Proton US Free 01");
            Assert.True(connectedOk);
            Assert.True(stateMachine.IsConnected);
            Assert.Equal(ConnectionState.Connected, stateMachine.CurrentState);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
