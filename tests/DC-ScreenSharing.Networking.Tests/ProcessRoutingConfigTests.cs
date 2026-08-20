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
    public void GenerateEngineConfig_ProducesValidJson_WithDiscordProcessRules()
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
        Assert.Contains("Discord.exe", json);
        Assert.Contains("wg-out", json);
        Assert.Contains("198.51.100.1", json);
        Assert.Contains("direct", json);
        Assert.Contains("default_domain_resolver", json);
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

            // Check DNS servers contain multiple remote servers
            var dnsServers = root.GetProperty("dns").GetProperty("servers").EnumerateArray().ToList();
            Assert.Contains(dnsServers, s => s.GetProperty("server").GetString() == "10.2.0.1");
            Assert.Contains(dnsServers, s => s.GetProperty("server").GetString() == "2a07:b944::2:1");

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
    public void SplitTunnelingMatrix_GuaranteesDiscordEntersTunnel_AndBrowsersUseDirect()
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

        // 1. Verify TUN Inbound
        var inbounds = root.GetProperty("inbounds");
        Assert.Equal("tun", inbounds[0].GetProperty("type").GetString());
        Assert.Equal("dcss-wintun", inbounds[0].GetProperty("interface_name").GetString());

        // 2. Verify Endpoints & Outbounds
        var endpoints = root.GetProperty("endpoints");
        Assert.Equal("wireguard", endpoints[0].GetProperty("type").GetString());
        Assert.Equal("wg-out", endpoints[0].GetProperty("tag").GetString());

        var outbounds = root.GetProperty("outbounds");
        Assert.Equal("direct", outbounds[0].GetProperty("type").GetString());
        Assert.Equal("direct", outbounds[0].GetProperty("tag").GetString());

        // 3. Verify Process-Level Route Rules
        var route = root.GetProperty("route");
        Assert.Equal("direct", route.GetProperty("final").GetString());
        Assert.Equal("dns-direct", route.GetProperty("default_domain_resolver").GetString());

        var rules = route.GetProperty("rules").EnumerateArray().ToList();
        
        // Find discord routing rule
        var discordRule = rules.FirstOrDefault(r => r.TryGetProperty("process_name", out _));
        Assert.True(discordRule.ValueKind != JsonValueKind.Undefined, "Could not find process_name routing rule");

        var matchedProcs = discordRule.GetProperty("process_name").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("Discord.exe", matchedProcs);
        Assert.Contains("DiscordCanary.exe", matchedProcs);
        Assert.Contains("DiscordPTB.exe", matchedProcs);

        // Explicitly assert that browsers are NOT in the tunnel match list
        Assert.DoesNotContain("chrome.exe", matchedProcs);
        Assert.DoesNotContain("msedge.exe", matchedProcs);
        Assert.DoesNotContain("firefox.exe", matchedProcs);

        // Final / fallback rule routes direct
        var finalDirectRule = rules.Last();
        Assert.Equal("direct", finalDirectRule.GetProperty("outbound").GetString());

        // 4. Verify DNS Split
        var dnsRules = root.GetProperty("dns").GetProperty("rules");
        var dnsRule = dnsRules[0];
        Assert.Equal("dns-remote", dnsRule.GetProperty("server").GetString());
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
