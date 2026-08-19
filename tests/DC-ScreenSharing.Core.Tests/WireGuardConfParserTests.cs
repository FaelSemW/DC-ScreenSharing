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
        Assert.Equal("1.1.1.1, 8.8.8.8", parsed.Dns);
        Assert.Equal(1420, parsed.Mtu);
        Assert.Equal("c2VydmVycHVibGlja2V5MTIzNDU2Nzg5MDEyMzQ1Njc4OTA=", parsed.PeerPublicKey);
        Assert.Equal("vpn.prod.example.com", parsed.Endpoint);
        Assert.Equal(51820, parsed.Port);
        Assert.Equal("0.0.0.0/0, ::/0", parsed.AllowedIps);
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
}
