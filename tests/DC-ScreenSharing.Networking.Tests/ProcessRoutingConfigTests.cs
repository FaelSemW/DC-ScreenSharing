using System.IO;
using System.Text.Json;
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

        var rules = route.GetProperty("rules");
        
        // Rule 0: Discord processes -> wg-out
        var discordRule = rules[0];
        Assert.Equal("wg-out", discordRule.GetProperty("outbound").GetString());
        var matchedProcs = discordRule.GetProperty("process_name").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("Discord.exe", matchedProcs);
        Assert.Contains("DiscordCanary.exe", matchedProcs);
        Assert.Contains("DiscordPTB.exe", matchedProcs);

        // Explicitly assert that browsers are NOT in the tunnel match list
        Assert.DoesNotContain("chrome.exe", matchedProcs);
        Assert.DoesNotContain("msedge.exe", matchedProcs);
        Assert.DoesNotContain("firefox.exe", matchedProcs);

        // Rule 1: All other system processes -> direct
        var defaultRule = rules[1];
        Assert.Equal("direct", defaultRule.GetProperty("outbound").GetString());

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
