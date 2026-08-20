using DCScreenSharing.Core.Profiles;
using Xunit;

namespace DCScreenSharing.Core.Tests;

public class WireGuardConfParserTests
{
    [Fact]
    public void Parse_StandardWireGuardConf_ExtractsAllFieldsCorrectly()
    {
        var conf = @"
[Interface]
PrivateKey = aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=
Address = 10.8.0.5/32
DNS = 1.1.1.1, 8.8.8.8
MTU = 1420

[Peer]
PublicKey = c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=
Endpoint = vpn.prod.example.com:51820
AllowedIPs = 0.0.0.0/0, ::/0
";

        var parsed = WireGuardConfParser.Parse(conf);

        Assert.Equal("aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=", parsed.PrivateKey);
        Assert.Equal("10.8.0.5/32", parsed.Address);
        Assert.Equal(new[] { "10.8.0.5/32" }, parsed.Addresses);
        Assert.Equal("1.1.1.1, 8.8.8.8", parsed.Dns);
        Assert.Equal(new[] { "1.1.1.1", "8.8.8.8" }, parsed.DnsServers);
        Assert.Equal(1420, parsed.Mtu);
        Assert.Equal("c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=", parsed.PeerPublicKey);
        Assert.Equal("vpn.prod.example.com", parsed.Endpoint);
        Assert.Equal(51820, parsed.Port);
        Assert.Equal("0.0.0.0/0, ::/0", parsed.AllowedIps);
        Assert.Equal(new[] { "0.0.0.0/0", "::/0" }, parsed.AllowedIpsList);
    }

    [Fact]
    public void Parse_ProtonWireGuardProfile_ExtractsMultiValueFieldsAsListsAndPreservesComments()
    {
        var protonConf = @"
# ProtonVPN WireGuard Configuration
# Profile generated for Proton user

[Interface]
# Client Private Key
PrivateKey = oPPdF6dRTfRTqAyaCgM0ZiJW9riRBUzMPI0Xo+bXK0Y=

# Dual-stack addresses
Address = 10.2.0.2/32, 2a07:b944::2:2/128

# Dual-stack DNS servers
DNS = 10.2.0.1, 2a07:b944::2:1

[Peer]
# Server Public Key
PublicKey = YLaLJahXZ6NuASXQLPl0eUPVAypirpaLuuO7tZa2bmo=

# Dual-stack routing
AllowedIPs = 0.0.0.0/0, ::/0

# Proton server endpoint
Endpoint = 185.159.158.1:51820

# Keepalive for NAT traversal
PersistentKeepalive = 25
";

        var parsed = WireGuardConfParser.Parse(protonConf);

        Assert.Equal("oPPdF6dRTfRTqAyaCgM0ZiJW9riRBUzMPI0Xo+bXK0Y=", parsed.PrivateKey);
        Assert.Equal(2, parsed.Addresses.Count);
        Assert.Equal("10.2.0.2/32", parsed.Addresses[0]);
        Assert.Equal("2a07:b944::2:2/128", parsed.Addresses[1]);

        Assert.Equal(2, parsed.DnsServers.Count);
        Assert.Equal("10.2.0.1", parsed.DnsServers[0]);
        Assert.Equal("2a07:b944::2:1", parsed.DnsServers[1]);

        Assert.Equal(2, parsed.AllowedIpsList.Count);
        Assert.Equal("0.0.0.0/0", parsed.AllowedIpsList[0]);
        Assert.Equal("::/0", parsed.AllowedIpsList[1]);

        Assert.Equal("185.159.158.1", parsed.Endpoint);
        Assert.Equal(51820, parsed.Port);
        Assert.Equal(25, parsed.PersistentKeepalive);
    }

    [Fact]
    public void Parse_IPv6Endpoint_ExtractsHostAndPortCorrectly()
    {
        var conf = @"
[Interface]
PrivateKey = aGVsbG93b3JsZHByaXZhdGVrZXkxMjM0NTY3ODkwMTI=
Address = fd00::2/64

[Peer]
PublicKey = c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=
Endpoint = [2001:db8::1]:51820
";

        var parsed = WireGuardConfParser.Parse(conf);

        Assert.Equal("2001:db8::1", parsed.Endpoint);
        Assert.Equal(51820, parsed.Port);
    }

    [Fact]
    public void Parse_EndpointVariations_IPv4_Hostname_BracketedIPv6_WithoutPort()
    {
        var conf1 = @"
[Interface]
PrivateKey = key1
Address = 10.0.0.1/32
[Peer]
PublicKey = key2
Endpoint = 192.168.1.1
";
        var parsed1 = WireGuardConfParser.Parse(conf1);
        Assert.Equal("192.168.1.1", parsed1.Endpoint);
        Assert.Equal(51820, parsed1.Port);

        var conf2 = @"
[Interface]
PrivateKey = key1
Address = 10.0.0.1/32
[Peer]
PublicKey = key2
Endpoint = node-nl-01.protonvpn.net
";
        var parsed2 = WireGuardConfParser.Parse(conf2);
        Assert.Equal("node-nl-01.protonvpn.net", parsed2.Endpoint);
        Assert.Equal(51820, parsed2.Port);

        var conf3 = @"
[Interface]
PrivateKey = key1
Address = 10.0.0.1/32
[Peer]
PublicKey = key2
Endpoint = [2a07:b944::1]
";
        var parsed3 = WireGuardConfParser.Parse(conf3);
        Assert.Equal("2a07:b944::1", parsed3.Endpoint);
        Assert.Equal(51820, parsed3.Port);
    }

    [Fact]
    public void ParseCidrList_ValidatesCIDRIndependently()
    {
        var validCidrs = " 10.2.0.2/32 , 2a07:b944::2:2/128 , 0.0.0.0/0 , ::/0 ";
        var list = WireGuardConfParser.ParseCidrList(validCidrs);

        Assert.Equal(4, list.Count);
        Assert.Equal("10.2.0.2/32", list[0]);
        Assert.Equal("2a07:b944::2:2/128", list[1]);
        Assert.Equal("0.0.0.0/0", list[2]);
        Assert.Equal("::/0", list[3]);
    }
}
