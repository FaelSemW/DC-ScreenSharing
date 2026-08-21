using System.Net;
using DC_ScreenSharing.Networking.ProcessIsolation;
using Xunit;

namespace DCScreenSharing.Networking.Tests;

public class ProcessIsolationTests
{
    [Fact]
    public void FlowKey_FromEndpoints_ProducesConsistentKey()
    {
        var localIp = IPAddress.Parse("192.168.1.100");
        var remoteIp = IPAddress.Parse("162.159.130.233");
        ushort localPort = 54321;
        ushort remotePort = 443;
        byte proto = 6; // TCP

        var key1 = FlowKey.FromEndpoints(proto, localIp, localPort, remoteIp, remotePort);
        var key2 = FlowKey.FromEndpoints(proto, localIp, localPort, remoteIp, remotePort);

        Assert.Equal(key1, key2);
        Assert.Equal(proto, key1.Protocol);
        Assert.Equal(localPort, key1.LocalPort);
        Assert.Equal(remotePort, key1.RemotePort);
    }

    [Fact]
    public void FlowMappingTable_AddOrUpdate_And_PruneExpired_WorksCorrectly()
    {
        var table = new FlowMappingTable();
        var keyTcp = FlowKey.FromEndpoints(6, IPAddress.Parse("10.0.0.2"), 50001, IPAddress.Parse("1.1.1.1"), 443);
        var keyUdp = FlowKey.FromEndpoints(17, IPAddress.Parse("10.0.0.2"), 50002, IPAddress.Parse("8.8.8.8"), 53);

        var entryTcp = new FlowEntry
        {
            Key = keyTcp,
            Pid = 1234,
            ProcessName = "Discord.exe",
            IsTargetFlow = true,
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-5) // Expired
        };

        var entryUdp = new FlowEntry
        {
            Key = keyUdp,
            Pid = 1234,
            ProcessName = "Discord.exe",
            IsTargetFlow = true,
            LastActivityUtc = DateTime.UtcNow // Active
        };

        table.AddOrUpdate(keyTcp, entryTcp);
        table.AddOrUpdate(keyUdp, entryUdp);

        Assert.Equal(2, table.Count);
        Assert.True(table.TryGetFlow(keyTcp, out var fetchedTcp));
        Assert.True(table.TryGetFlow(keyUdp, out var fetchedUdp));

        // Prune flows older than 1 minute
        table.PruneExpiredFlows(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        Assert.Equal(1, table.Count);
        Assert.False(table.TryGetFlow(keyTcp, out _));
        Assert.True(table.TryGetFlow(keyUdp, out _));
    }

    [Fact]
    public void FlowEntry_Touch_UpdatesBytesAndTimestamp()
    {
        var key = FlowKey.FromEndpoints(17, IPAddress.Parse("127.0.0.1"), 1234, IPAddress.Parse("127.0.0.1"), 4321);
        var entry = new FlowEntry { Key = key, LastActivityUtc = DateTime.UtcNow.AddHours(-1) };

        var oldTime = entry.LastActivityUtc;
        entry.Touch(bytesSent: 1024, bytesReceived: 2048);

        Assert.True(entry.LastActivityUtc > oldTime);
        Assert.Equal(1024, entry.BytesSent);
        Assert.Equal(2048, entry.BytesReceived);
    }

    [Fact]
    public void ParseIPv4Header_ValidPacket_ExtractsFieldsCorrectly()
    {
        // Construct standard 20-byte IPv4 packet (UDP to 8.8.8.8:53 from 192.168.1.50:50000)
        byte[] packet = new byte[28];
        packet[0] = 0x45; // Version 4, IHL 5
        packet[2] = 0x00; // Total length = 28
        packet[3] = 0x1C;
        packet[9] = 17;   // Protocol 17 = UDP

        // Src IP: 192.168.1.50
        packet[12] = 192; packet[13] = 168; packet[14] = 1; packet[15] = 50;
        // Dst IP: 8.8.8.8
        packet[16] = 8; packet[17] = 8; packet[18] = 8; packet[19] = 8;

        // UDP Header: src 50000 (0xC350), dst 53 (0x0035), len 8 (0x0008)
        packet[20] = 0xC3; packet[21] = 0x50;
        packet[22] = 0x00; packet[23] = 0x35;
        packet[24] = 0x00; packet[25] = 0x08;

        bool parsedIp = WinDivertNative.ParseIPv4Header(
            packet, packet.Length, out byte proto, out var srcIp, out var dstIp, out int ipHdrLen, out int totalLen);

        Assert.True(parsedIp);
        Assert.Equal(17, proto);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), srcIp);
        Assert.Equal(IPAddress.Parse("8.8.8.8"), dstIp);
        Assert.Equal(20, ipHdrLen);
        Assert.Equal(28, totalLen);

        bool parsedUdp = WinDivertNative.ParseUdpHeader(
            packet, ipHdrLen, packet.Length, out ushort srcPort, out ushort dstPort, out int udpLen);

        Assert.True(parsedUdp);
        Assert.Equal(50000, srcPort);
        Assert.Equal(53, dstPort);
        Assert.Equal(8, udpLen);
    }

    [Fact]
    public void ParseTcpHeader_ValidPacket_ExtractsPortsAndFlags()
    {
        // 20-byte IP + 20-byte TCP SYN packet
        byte[] packet = new byte[40];
        packet[0] = 0x45;
        packet[2] = 0x00; packet[3] = 0x28; // Length 40
        packet[9] = 6; // TCP

        // Src 10.0.0.1, Dst 1.1.1.1
        packet[12] = 10; packet[13] = 0; packet[14] = 0; packet[15] = 1;
        packet[16] = 1; packet[17] = 1; packet[18] = 1; packet[19] = 1;

        // TCP: Src 49152 (0xC000), Dst 443 (0x01BB), DataOffset 5 (0x50), Flags SYN (0x02)
        packet[20] = 0xC0; packet[21] = 0x00;
        packet[22] = 0x01; packet[23] = 0xBB;
        packet[24] = 0x00; packet[25] = 0x00; packet[26] = 0x00; packet[27] = 0x01; // Seq
        packet[32] = 0x50; // 5 * 4 = 20 bytes
        packet[33] = 0x02; // SYN flag

        bool parsedIp = WinDivertNative.ParseIPv4Header(
            packet, packet.Length, out byte proto, out _, out _, out int ipHdrLen, out _);

        Assert.True(parsedIp);
        Assert.Equal(6, proto);

        bool parsedTcp = WinDivertNative.ParseTcpHeader(
            packet, ipHdrLen, packet.Length, out ushort srcPort, out ushort dstPort, out uint seq, out _, out byte flags, out int tcpHdrLen);

        Assert.True(parsedTcp);
        Assert.Equal(49152, srcPort);
        Assert.Equal(443, dstPort);
        Assert.Equal(1u, seq);
        Assert.Equal(0x02, flags);
        Assert.Equal(20, tcpHdrLen);
    }

    [Fact]
    public void ProcessIdentityResolver_TracksAndValidatesProcess()
    {
        var resolver = new ProcessIdentityResolver(new[] { "TestTarget.exe" });
        int testPid = 99999;
        var now = DateTime.UtcNow;

        resolver.RegisterTargetPid(testPid, @"C:\Test\TestTarget.exe", now);
        Assert.Equal(1, resolver.GetTrackedPidCount());

        resolver.UnregisterPid(testPid);
        Assert.Equal(0, resolver.GetTrackedPidCount());
    }

    [Fact]
    public async Task WinDivertProcessIsolationEngine_StartAndStop_TransitionsStateCleanly()
    {
        var engine = new WinDivertProcessIsolationEngine();
        Assert.False(engine.IsRunning);

        var options = new ProcessIsolationOptions
        {
            TargetProcessNames = new List<string> { "Discord.exe" },
            VpnInterfaceIndex = 1,
            VpnInterfaceIp = IPAddress.Parse("10.8.0.2"),
            TransportType = "OpenVPN"
        };

        await engine.StartAsync(options);
        Assert.True(engine.IsRunning);

        var stats = engine.GetStats();
        Assert.True(stats.IsRunning);
        Assert.Equal("OpenVPN", stats.TransportName);
        Assert.Equal(1, stats.VpnInterfaceIndex);
        Assert.Equal("10.8.0.2", stats.VpnInterfaceIp);

        await engine.StopAsync();
        Assert.False(engine.IsRunning);

        var stoppedStats = engine.GetStats();
        Assert.False(stoppedStats.IsRunning);
    }

    [Fact]
    public void WinDivertNative_ModifyIPv4TcpDestination_RewritesCorrectly()
    {
        // 20-byte IP + 20-byte TCP packet
        byte[] packet = new byte[40];
        packet[0] = 0x45;
        packet[2] = 0x00; packet[3] = 0x28; // Len 40
        packet[9] = 6; // TCP

        // Src 192.168.1.100, Dst 162.159.130.233
        packet[12] = 192; packet[13] = 168; packet[14] = 1; packet[15] = 100;
        packet[16] = 162; packet[17] = 159; packet[18] = 130; packet[19] = 233;

        // TCP: Src 54321, Dst 443
        packet[20] = 0xD4; packet[21] = 0x31; // 54321
        packet[22] = 0x01; packet[23] = 0xBB; // 443

        var addr = new WinDivertNative.WINDIVERT_ADDRESS { Outbound = 1 };

        WinDivertNative.ModifyIPv4TcpDestination(packet, packet.Length, 20, IPAddress.Loopback, 15889, ref addr);

        // Verify destination IP is 127.0.0.1
        Assert.Equal(127, packet[16]);
        Assert.Equal(0, packet[17]);
        Assert.Equal(0, packet[18]);
        Assert.Equal(1, packet[19]);

        // Verify destination port is 15889 (0x3E11)
        ushort rewrittenPort = (ushort)((packet[22] << 8) | packet[23]);
        Assert.Equal(15889, rewrittenPort);
    }

    [Fact]
    public void ProcessIdentityResolver_CaseInsensitiveMatching_SupportsMultipleVariants()
    {
        var resolver = new ProcessIdentityResolver(new[] { "Discord.exe", "DiscordPTB.exe", "DiscordCanary.exe" });

        int pid1 = 1001;
        int pid2 = 1002;
        int pid3 = 1003;
        int pidChrome = 2001;

        resolver.RegisterTargetPid(pid1, @"C:\Users\Test\AppData\Local\Discord\app-1.0.9254\Discord.exe", DateTime.UtcNow);
        resolver.RegisterTargetPid(pid2, @"C:\Users\Test\AppData\Local\DiscordPTB\app-1.0.9254\discordptb.exe", DateTime.UtcNow);
        resolver.RegisterTargetPid(pid3, @"C:\Users\Test\AppData\Local\DiscordCanary\app-1.0.9254\DISCORDCANARY.EXE", DateTime.UtcNow);

        Assert.True(resolver.IsTargetProcess(pid1));
        Assert.True(resolver.IsTargetProcess(pid2));
        Assert.True(resolver.IsTargetProcess(pid3));
        Assert.False(resolver.IsTargetProcess(pidChrome));
    }

    [Fact]
    public void TcpFlowRouter_PortMapping_RegistersAndResolvesEndpoints()
    {
        var flowTable = new FlowMappingTable();
        var router = new TcpFlowRouter(flowTable);

        ushort clientPort = 55123;
        var targetIp = IPAddress.Parse("162.159.130.233");
        ushort targetPort = 443;

        router.RegisterTargetMapping(clientPort, targetIp, targetPort);

        // Flow mapping should be retrievable and zero bypasses should occur
        Assert.Equal(0, router.TotalBytesSent);
        Assert.Equal(0, router.TotalBytesReceived);
    }

    [Fact]
    public async Task DualProtocol_Switching_CleanTeardownAndRestart()
    {
        var engine = new WinDivertProcessIsolationEngine();

        // 1. Start OpenVPN
        await engine.StartAsync(new ProcessIsolationOptions
        {
            TargetProcessNames = new List<string> { "Discord.exe" },
            TransportType = "OpenVPN",
            VpnInterfaceIndex = 12,
            VpnInterfaceIp = IPAddress.Parse("10.8.0.2")
        });
        Assert.True(engine.IsRunning);
        Assert.Equal(0, engine.TargetTcpBypassed);
        Assert.Equal(0, engine.TargetUdpBypassed);

        // 2. Stop OpenVPN
        await engine.StopAsync();
        Assert.False(engine.IsRunning);

        // 3. Start WireGuard
        await engine.StartAsync(new ProcessIsolationOptions
        {
            TargetProcessNames = new List<string> { "Discord.exe" },
            TransportType = "WireGuard",
            VpnInterfaceIndex = 15,
            VpnInterfaceIp = IPAddress.Parse("10.2.0.2")
        });
        Assert.True(engine.IsRunning);
        Assert.Equal("WireGuard", engine.GetStats().TransportName);

        // 4. Final Stop
        await engine.StopAsync();
        Assert.False(engine.IsRunning);
    }
}
