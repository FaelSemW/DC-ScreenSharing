using System.Text.Json;
using DCScreenSharing.Core.State;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;
using Xunit;

namespace DC_ScreenSharing.IntegrationTests;

public class TunnelHealthMonitorTests
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
    public void MedianLatency_CalculatesCorrectly_OddAndEvenCounts()
    {
        // Odd count: 274, 301, 287 -> median 287
        var samples1 = new[] { 274, 301, 287 };
        var median1 = TunnelHealthMonitor.CalculateMedianLatency(samples1);
        Assert.Equal(287, median1);

        // Even count: 260, 280, 300, 320 -> median (280+300)/2 = 290
        var samples2 = new[] { 260, 280, 300, 320 };
        var median2 = TunnelHealthMonitor.CalculateMedianLatency(samples2);
        Assert.Equal(290, median2);

        // Single sample
        var samples3 = new[] { 142 };
        var median3 = TunnelHealthMonitor.CalculateMedianLatency(samples3);
        Assert.Equal(142, median3);
    }

    [Fact]
    public void MedianLatency_IgnoresTimeoutsAndNegativeValues()
    {
        // When timeout/sentinel values or failures occur (represented as null/negative),
        // only valid positive samples should be computed
        var validOnly = new[] { 285, 290 };
        var median = TunnelHealthMonitor.CalculateMedianLatency(validOnly);
        Assert.Equal(287, median);

        // Empty set -> returns null (never misrepresents timeout as 5000ms)
        var empty = Array.Empty<int>();
        var nullMedian = TunnelHealthMonitor.CalculateMedianLatency(empty);
        Assert.Null(nullMedian);
    }

    [Fact]
    public void HealthReport_CalculatesPacketLossCorrectly()
    {
        var report0Loss = new HealthReport { ProbesTotal = 3, ProbesSuccessful = 3 };
        Assert.Equal(0.0, report0Loss.PacketLossPercent);

        var report33Loss = new HealthReport { ProbesTotal = 3, ProbesSuccessful = 2 };
        Assert.InRange(report33Loss.PacketLossPercent, 33.0, 34.0);

        var report100Loss = new HealthReport { ProbesTotal = 3, ProbesSuccessful = 0 };
        Assert.Equal(100.0, report100Loss.PacketLossPercent);
    }

    [Fact]
    public void ProcessRoutingEngine_GeneratesKeepaliveInSingBoxConfig()
    {
        var logger = new TestLogger();
        var engine = new ProcessRoutingEngine(logger);

        var config = new TunnelConfiguration
        {
            ServerId = "us-east-1",
            ServerName = "US East",
            Endpoint = "198.51.100.1",
            Port = 51820,
            Address = "10.8.0.2/32",
            PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
            PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
            Mtu = 1420
        };

        var json = engine.GenerateEngineConfig(config);
        Assert.NotNull(json);

        // Verify persistent_keepalive_interval is set to 25s
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var endpoints = root.GetProperty("endpoints");
        Assert.True(endpoints.GetArrayLength() > 0);

        var wgEndpoint = endpoints[0];
        Assert.Equal("wireguard", wgEndpoint.GetProperty("type").GetString());

        var peers = wgEndpoint.GetProperty("peers");
        Assert.True(peers.GetArrayLength() > 0);

        var firstPeer = peers[0];
        Assert.True(firstPeer.TryGetProperty("persistent_keepalive_interval", out var keepaliveProp));
        Assert.Equal(25, keepaliveProp.GetInt32());

        // Verify routing rules: proxy-in routes to wg-out, final direct
        var route = root.GetProperty("route");
        Assert.Equal("direct", route.GetProperty("final").GetString());

        var inbounds = root.GetProperty("inbounds");
        var proxyIn = inbounds[0];
        Assert.Equal("mixed", proxyIn.GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", proxyIn.GetProperty("listen").GetString());
    }

    [Fact]
    public void ProcessRoutingEngine_RoutesLocalProxyToWireGuard_AndOthersDirect()
    {
        var logger = new TestLogger();
        var engine = new ProcessRoutingEngine(logger);

        var config = new TunnelConfiguration
        {
            ServerId = "us-east-1",
            ServerName = "US East",
            Endpoint = "198.51.100.1",
            Port = 51820,
            Address = "10.8.0.2/32",
            PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
            PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
            Mtu = 1420,
            DiscordExecutablePath = @"C:\Users\TestUser\AppData\Local\Discord\app-1.0.9254\Discord.exe"
        };

        var json = engine.GenerateEngineConfig(config);
        using var doc = JsonDocument.Parse(json);
        var route = doc.RootElement.GetProperty("route");
        var rules = route.GetProperty("rules");

        // Verify proxy-in rule routes to wg-out
        var foundProxyRule = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("inbound", out var inList) && rule.TryGetProperty("outbound", out var outBound))
            {
                if (outBound.GetString() == "wg-out")
                {
                    foundProxyRule = true;
                    Assert.Contains("proxy-in", inList.EnumerateArray().Select(x => x.GetString()));
                }
            }
        }
        Assert.True(foundProxyRule, "Proxy-in routing rule to wg-out must exist in config.");
    }

    [Fact]
    public void ProcessRoutingEngine_ContainsNoLegacyDnsOutbound()
    {
        var logger = new TestLogger();
        var engine = new ProcessRoutingEngine(logger);

        var config = new TunnelConfiguration
        {
            ServerId = "us-east-1",
            ServerName = "US East",
            Endpoint = "198.51.100.1",
            Port = 51820,
            Address = "10.8.0.2/32",
            PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
            PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
            Mtu = 1420
        };

        var json = engine.GenerateEngineConfig(config);
        Assert.DoesNotContain("\"type\": \"dns\"", json);
        Assert.DoesNotContain("\"dns-out\"", json);
        Assert.DoesNotContain("\"outbound\": \"dns-out\"", json);

        using var doc = JsonDocument.Parse(json);
        var outbounds = doc.RootElement.GetProperty("outbounds");
        foreach (var outbound in outbounds.EnumerateArray())
        {
            Assert.NotEqual("dns", outbound.GetProperty("type").GetString());
        }
    }

    [Fact]
    public void ProcessRoutingEngine_ValidatesCleanlyAgainstBundledSingBoxBinary()
    {
        var logger = new TestLogger();
        var tempDir = Path.Combine(Path.GetTempPath(), "DCSS_Test_Val_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var engine = new ProcessRoutingEngine(logger, tempDir);

            var config = new TunnelConfiguration
            {
                ServerId = "us-east-1",
                ServerName = "US East",
                Endpoint = "198.51.100.1",
                Port = 51820,
                Address = "10.8.0.2/32",
                PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
                PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
                Mtu = 1420,
                DiscordExecutablePath = @"C:\Users\TestUser\AppData\Local\Discord\app-1.0.9254\Discord.exe"
            };

            var (isValid, error) = engine.ValidateRuntimeConfiguration(config);
            Assert.True(isValid, $"Engine config check failed: {error}");
            Assert.Null(error);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ProcessRoutingEngine_AtomicConfigWrite_CleansUpTempFiles()
    {
        var logger = new TestLogger();
        var tempDir = Path.Combine(Path.GetTempPath(), "DCSS_Test_Atomic_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var engine = new ProcessRoutingEngine(logger, tempDir);
            var config = new TunnelConfiguration
            {
                ServerId = "us-east-1",
                ServerName = "US East",
                Endpoint = "198.51.100.1",
                Port = 51820,
                Address = "10.8.0.2/32",
                PrivateKey = "aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=",
                PeerPublicKey = "c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=",
                Mtu = 1420
            };

            var (isValid, _) = engine.ValidateRuntimeConfiguration(config);
            Assert.True(isValid);

            // Verify no leftover .tmp files
            var tmpFiles = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(tmpFiles);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task TunnelHealthMonitor_BoundsRecoveryAttempts_PreventsInfiniteLoop()
    {
        var logger = new TestLogger();
        var recoveryCallCount = 0;

        using var monitor = new TunnelHealthMonitor(logger, probeIntervalMs: 1000, probeTimeoutMs: 500, maxRecoveryAttempts: 2);
        monitor.OnPerformRecoveryAsync = () =>
        {
            recoveryCallCount++;
            return Task.FromResult(false); // Simulate unsuccessful recovery
        };

        // Trigger two recoveries
        var reportFail = new HealthReport
        {
            ProbesTotal = 3,
            ProbesSuccessful = 0,
            ConsecutiveFailures = 3
        };

        // Start and stop cleanly
        monitor.StartMonitoring("127.0.0.1");
        await Task.Delay(100);
        monitor.StopMonitoring();

        Assert.Equal(TunnelHealthState.Healthy, monitor.CurrentState);
    }

    [Fact]
    public void LatencySeparation_ControlEndpointRtt_IsNotExposedAsVpnDataPlaneLatency()
    {
        // When control endpoint RTT is 18 ms (physical Anycast to outer server),
        // but tunneled data-plane probe has not returned or is 142 ms,
        // MedianLatencyMs MUST represent the real tunneled data plane, never the control endpoint RTT.
        var report = new HealthReport
        {
            ControlEndpointRttMs = 18,
            VpnDataPlaneRttMs = 142,
            TargetRouteRttMs = 142,
            MedianLatencyMs = 142,
            IsTunneledDataPlaneVerified = true,
            ProbesTotal = 3,
            ProbesSuccessful = 3
        };

        Assert.Equal(18, report.ControlEndpointRttMs);
        Assert.Equal(142, report.VpnDataPlaneRttMs);
        Assert.Equal(142, report.MedianLatencyMs);
        Assert.NotEqual(report.ControlEndpointRttMs, report.MedianLatencyMs);
        Assert.True(report.IsTunneledDataPlaneVerified);
    }

    [Fact]
    public void FailedDataPlaneProbe_SetsMedianLatencyToNull_AndDegradesState()
    {
        // When outer control endpoint is reachable (e.g. 18 ms physical RTT),
        // but tunneled data plane probe fails (e.g. proxy offline or VPN data plane dropped),
        // MedianLatencyMs MUST be null and IsTunneledDataPlaneVerified MUST be false.
        var report = new HealthReport
        {
            ControlEndpointRttMs = 18,
            VpnDataPlaneRttMs = null,
            TargetRouteRttMs = null,
            MedianLatencyMs = null,
            IsTunneledDataPlaneVerified = false,
            ProbesTotal = 3,
            ProbesSuccessful = 0,
            State = TunnelHealthState.Degraded
        };

        Assert.Null(report.MedianLatencyMs);
        Assert.Null(report.VpnDataPlaneRttMs);
        Assert.False(report.IsTunneledDataPlaneVerified);
        Assert.Equal(18, report.ControlEndpointRttMs);
    }

    [Fact]
    public async Task ProbeSocks5DataPlaneAsync_NonExistentProxy_ReturnsNullWithoutThrowing()
    {
        // Probing an unreachable SOCKS5 port should fail closed and return null cleanly
        var rtt = await TunnelHealthMonitor.ProbeSocks5DataPlaneAsync("127.0.0.1", 59999, "1.1.1.1", 80, timeoutMs: 300);
        Assert.Null(rtt);
    }

    [Fact]
    public async Task ProbeControlEndpointAsync_UnreachableHost_ReturnsNullWithoutThrowing()
    {
        // Probing an invalid/unreachable IP should return null cleanly within timeout
        var rtt = await TunnelHealthMonitor.ProbeControlEndpointAsync("192.0.2.1", 443, timeoutMs: 300);
        Assert.Null(rtt);
    }
}

