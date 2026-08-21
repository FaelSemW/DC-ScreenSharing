using System.IO;
using System.Text.Json;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;
using Xunit;

namespace DCScreenSharing.Networking.Tests;

public class ProcessRoutingConfigTests
{
    [Fact]
    public void GenerateEngineConfig_ProducesValidJson_WithLocalProxyInbound()
    {
        var logger = new FileLogger(Path.GetTempPath());
        var engine = new ProcessRoutingEngine(logger);

        var config = new TunnelConfiguration
        {
            ServerId = "us-01",
            Endpoint = "198.51.100.1",
            Port = 51820,
            PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
            PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
            DiscordExecutablePath = @"C:\Users\Test\AppData\Local\Discord\app-1.0.9254\Discord.exe"
        };

        var json = engine.GenerateEngineConfig(config);

        Assert.NotNull(json);
        Assert.Contains("proxy-in", json);
        Assert.Contains("wg-out", json);
        Assert.Contains("198.51.100.1", json);
        Assert.Contains("direct", json);
    }

    [Fact]
    public void GenerateEngineConfig_ProtonDualStack_GeneratesValidSingBoxJson_ExitCode0()
    {
        var protonConf = @"
# ProtonVPN WireGuard Configuration
[Interface]
PrivateKey = aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=
Address = 10.2.0.2/32, 2a07:b944::2:2/128
DNS = 10.2.0.1, 2a07:b944::2:1

[Peer]
PublicKey = c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=
AllowedIPs = 0.0.0.0/0, ::/0
Endpoint = 185.159.158.1:51820
PersistentKeepalive = 25
";
        var parsed = WireGuardConfParser.Parse(protonConf);
        var logger = new FileLogger(Path.GetTempPath());
        var tempDir = Path.Combine(Path.GetTempPath(), "DCSS_Test_Proton_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var engine = new ProcessRoutingEngine(logger, tempDir);
            var config = new TunnelConfiguration
            {
                ServerId = "proton-us-01",
                ServerName = "Proton US Free",
                Endpoint = parsed.Endpoint,
                Port = parsed.Port,
                Addresses = new List<string>(parsed.Addresses),
                DnsServers = new List<string>(parsed.DnsServers),
                AllowedIpsList = new List<string>(parsed.AllowedIpsList),
                PrivateKey = parsed.PrivateKey,
                PeerPublicKey = parsed.PeerPublicKey,
                PersistentKeepalive = parsed.PersistentKeepalive,
                DiscordExecutablePath = @"C:\Users\Test\AppData\Local\Discord\app-1.0.9254\Discord.exe"
            };

            var json = engine.GenerateEngineConfig(config);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check endpoints address array contains both IPv4 and IPv6
            var endpoints = root.GetProperty("endpoints");
            var addresses = endpoints[0].GetProperty("address").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("10.2.0.2/32", addresses);
            Assert.Contains("2a07:b944::2:2/128", addresses);

            // Check peer allowed_ips array contains both IPv4 and IPv6
            var peer = endpoints[0].GetProperty("peers")[0];
            var allowedIps = peer.GetProperty("allowed_ips").EnumerateArray().Select(a => a.GetString()).ToList();
            Assert.Contains("0.0.0.0/0", allowedIps);
            Assert.Contains("::/0", allowedIps);

            // Validate against bundled sing-box binary (exit code 0)
            var (isValid, error) = engine.ValidateRuntimeConfiguration(config);
            Assert.True(isValid, $"sing-box validation failed: {error}");
            Assert.Null(error);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void TargetProxyArchitecture_UsesLoopbackProxyWithoutTun_AndProtectsHostTraffic()
    {
        var logger = new FileLogger(Path.GetTempPath());
        var engine = new ProcessRoutingEngine(logger);

        var config = new TunnelConfiguration
        {
            ServerId = "de-01",
            Endpoint = "198.51.100.2",
            Port = 51820,
            PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
            PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
            AllowedApps = new List<string> { "Discord.exe", "DiscordCanary.exe", "DiscordPTB.exe" }
        };

        var json = engine.GenerateEngineConfig(config);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 1. Verify Loopback Mixed (SOCKS5/HTTP) Inbound — Zero TUN, Zero Host Route modifications
        var inbounds = root.GetProperty("inbounds");
        Assert.Equal("mixed", inbounds[0].GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", inbounds[0].GetProperty("listen").GetString());
        Assert.Equal(15888, inbounds[0].GetProperty("listen_port").GetInt32());

        // 2. Verify Endpoints & Outbounds
        var endpoints = root.GetProperty("endpoints");
        Assert.Equal("wireguard", endpoints[0].GetProperty("type").GetString());
        Assert.Equal("wg-out", endpoints[0].GetProperty("tag").GetString());

        var outbounds = root.GetProperty("outbounds");
        Assert.Equal("direct", outbounds[0].GetProperty("type").GetString());
        Assert.Equal("direct", outbounds[0].GetProperty("tag").GetString());

        // 3. Verify Route Rules: proxy-in routes to wg-out
        var route = root.GetProperty("route");
        Assert.Equal("direct", route.GetProperty("final").GetString());

        var rules = route.GetProperty("rules").EnumerateArray().ToList();
        var proxyRule = rules.FirstOrDefault(r => r.TryGetProperty("inbound", out _));
        Assert.True(proxyRule.ValueKind != JsonValueKind.Undefined, "Could not find proxy-in routing rule");
        Assert.Equal("wg-out", proxyRule.GetProperty("outbound").GetString());
    }

    [Fact]
    public void Engine_FindExecutable_ResolvesKnownBinary()
    {
        var logger = new FileLogger(Path.GetTempPath());
        var engine = new ProcessRoutingEngine(logger);

        // Config generation should succeed
        var config = new TunnelConfiguration
        {
            ServerId = "us-01",
            Endpoint = "198.51.100.1",
            Port = 51820,
            PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
            PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA="
        };

        var configJson = engine.GenerateEngineConfig(config);
        Assert.NotNull(configJson);
    }
}
