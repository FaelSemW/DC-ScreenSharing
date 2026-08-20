using System.Net;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.Security;
using Xunit;

namespace DCScreenSharing.Core.Tests;

public class OpenVpnConfigParserTests
{
    [Fact]
    public void Parse_GenericStandardClientProfile_Succeeds()
    {
        var ovpn = @"
client
dev tun
proto udp
remote vpn.example.com 1194
resolv-retry infinite
nobind
persist-key
persist-tun
remote-cert-tls server
cipher AES-256-GCM
auth SHA256
verb 3
<ca>
-----BEGIN CERTIFICATE-----
MIIB...CA_CERT...
-----END CERTIFICATE-----
</ca>
";

        var result = OpenVpnConfigParser.ParseAndValidate(ovpn);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("UDP", result.Protocol);
        Assert.Equal("vpn.example.com:1194", result.PrimaryRemote);
        Assert.Equal(0, result.AdditionalRemotesCount);
        Assert.Equal("None", result.AuthType);
        Assert.NotNull(result.ParsedConfig);
        Assert.Contains("BEGIN CERTIFICATE", result.ParsedConfig.CaCert);
        Assert.Equal("AES-256-GCM", result.ParsedConfig.Cipher);
        Assert.Equal("SHA256", result.ParsedConfig.Auth);
    }

    [Fact]
    public void Parse_ProtonVpnProfile_Succeeds()
    {
        var protonOvpn = @"
client
dev tun
proto udp
remote 185.159.157.1 1194
remote 185.159.157.1 4569
remote 185.159.157.1 5060
resolv-retry infinite
nobind
persist-key
persist-tun
cipher AES-256-GCM
data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305
auth SHA512
verb 3
auth-user-pass
tun-mtu 1500
mssfix 1450
<ca>
-----BEGIN CERTIFICATE-----
MIIProtonCA...
-----END CERTIFICATE-----
</ca>
<tls-auth>
-----BEGIN OpenVPN Static key V1-----
ProtonStaticKey...
-----END OpenVPN Static key V1-----
</tls-auth>
key-direction 1
";

        var result = OpenVpnConfigParser.ParseAndValidate(protonOvpn, declaredProvider: "Proton");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("UDP", result.Protocol);
        Assert.Equal("185.159.157.1:1194", result.PrimaryRemote);
        Assert.Equal(2, result.AdditionalRemotesCount);
        Assert.Equal(3, result.Remotes.Count);
        Assert.Equal(4569, result.Remotes[1].Port);
        Assert.Equal(5060, result.Remotes[2].Port);
        Assert.Equal("Username/Password", result.AuthType);
        Assert.Equal("Proton", result.Provider);
        Assert.True(result.ParsedConfig?.AuthUserPass);
        Assert.Equal("1", result.ParsedConfig?.KeyDirection);
        Assert.False(string.IsNullOrEmpty(result.ParsedConfig?.TlsAuthKey));
    }

    [Fact]
    public void Parse_VpnBookProfile_Succeeds()
    {
        var vpnBookOvpn = @"
client
dev tun
proto tcp-client
remote us16.vpnbook.com 443
resolv-retry infinite
nobind
persist-key
persist-tun
remote-cert-tls server
cipher AES-128-CBC
auth SHA1
auth-user-pass
verb 3
<ca>
-----BEGIN CERTIFICATE-----
MIIVpnBookCA...
-----END CERTIFICATE-----
</ca>
<cert>
-----BEGIN CERTIFICATE-----
MIIVpnBookClientCert...
-----END CERTIFICATE-----
</cert>
<key>
-----BEGIN PRIVATE KEY-----
MIIHighlySensitiveKey...
-----END PRIVATE KEY-----
</key>
";

        var result = OpenVpnConfigParser.ParseAndValidate(vpnBookOvpn, declaredProvider: "VPNBook");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("TCP", result.Protocol);
        Assert.Equal("us16.vpnbook.com:443", result.PrimaryRemote);
        Assert.Equal("Both", result.AuthType);
        Assert.Equal("VPNBook", result.Provider);
        Assert.NotNull(result.ParsedConfig);
        Assert.Contains("MIIVpnBookCA", result.ParsedConfig.CaCert);
        Assert.Contains("MIIVpnBookClientCert", result.ParsedConfig.ClientCert);
        Assert.Contains("MIIHighlySensitiveKey", result.ParsedConfig.ClientKey);
    }

    [Fact]
    public void Parse_IPv6AndMultipleRemotes_PreservesOrdering()
    {
        var ovpn = @"
client
dev tun
proto udp6
remote 2001:db8::1 1194
remote 192.0.2.1 1194 udp
remote [2001:db8::2] 443 tcp
<ca>
-----BEGIN CERTIFICATE-----
CA
-----END CERTIFICATE-----
</ca>
";

        var result = OpenVpnConfigParser.ParseAndValidate(ovpn);

        Assert.True(result.IsValid);
        Assert.True(result.HasIPv6);
        Assert.Equal(3, result.Remotes.Count);
        Assert.Equal("2001:db8::1", result.Remotes[0].Host);
        Assert.Equal("192.0.2.1", result.Remotes[1].Host);
        Assert.Equal("[2001:db8::2]", result.Remotes[2].Host);
        Assert.Equal("tcp", result.Remotes[2].Proto);
    }

    [Theory]
    [InlineData("script-security 2\nup evil.bat")]
    [InlineData("down \"C:\\malicious\\script.ps1\"")]
    [InlineData("route-up /bin/sh")]
    [InlineData("plugin /usr/lib/openvpn/plugins/openvpn-plugin-auth-pam.so")]
    [InlineData("plugin malicious.dll")]
    [InlineData("client-connect evil.exe")]
    [InlineData("tls-verify /tmp/verify.sh")]
    [InlineData("management 127.0.0.1 9999")]
    public void Parse_DangerousDirectives_AreRejected(string maliciousDirective)
    {
        var ovpn = $@"
client
dev tun
proto udp
remote vpn.example.com 1194
{maliciousDirective}
<ca>
-----BEGIN CERTIFICATE-----
CA
-----END CERTIFICATE-----
</ca>
";

        var result = OpenVpnConfigParser.ParseAndValidate(ovpn);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.UnsafeDirectives);
        Assert.Contains("contains disallowed or dangerous directives", result.Error);
    }

    [Theory]
    [InlineData("ca ..\\..\\Windows\\System32\\cmd.exe")]
    [InlineData("key ../../../etc/shadow")]
    [InlineData("auth-user-pass C:\\secrets\\passwords.txt")]
    public void Parse_PathTraversal_IsRejected(string maliciousRef)
    {
        var ovpn = $@"
client
dev tun
proto udp
remote vpn.example.com 1194
{maliciousRef}
";

        var result = OpenVpnConfigParser.ParseAndValidate(ovpn);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void CredentialCrypto_EncryptAndDecrypt_Succeeds()
    {
        var plain = "SuperSecretPassword123!@#";
        var encrypted = CredentialCrypto.Encrypt(plain);

        Assert.False(string.IsNullOrEmpty(encrypted));
        Assert.NotEqual(plain, encrypted);

        var decrypted = CredentialCrypto.Decrypt(encrypted);
        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void CredentialCrypto_WithCustomKey_Succeeds()
    {
        var plain = "vpnbook-rotated-password-2026";
        var key = "operator-master-railway-key-xyz";

        var encrypted = CredentialCrypto.Encrypt(plain, key);
        var decrypted = CredentialCrypto.Decrypt(encrypted, key);

        Assert.Equal(plain, decrypted);

        // Wrong key should fail to decrypt (AES-GCM tag mismatch)
        var failedDecrypted = CredentialCrypto.Decrypt(encrypted, "wrong-key");
        Assert.Equal(string.Empty, failedDecrypted);
    }
}
