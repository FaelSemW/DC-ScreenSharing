using DCScreenSharing.Core.Profiles;
using Xunit;

namespace DCScreenSharing.Core.Tests;

public class ProfileCryptoTests
{
    [Fact]
    public void GenerateKeyPair_ProducesNonEmptyKeys()
    {
        var (privKey, pubKey) = ProfileCrypto.GenerateKeyPair();
        Assert.False(string.IsNullOrWhiteSpace(privKey));
        Assert.False(string.IsNullOrWhiteSpace(pubKey));
        Assert.Contains("BEGIN PRIVATE KEY", privKey);
        Assert.Contains("BEGIN PUBLIC KEY", pubKey);
    }

    [Fact]
    public void SignAndVerify_ValidSignature_ReturnsTrue()
    {
        var (privKey, pubKey) = ProfileCrypto.GenerateKeyPair();
        var payload = "{\"generation\": 206, \"serverId\": \"us-01\"}";

        var signature = ProfileCrypto.SignData(payload, privKey);
        Assert.False(string.IsNullOrWhiteSpace(signature));

        var isValid = ProfileCrypto.VerifySignature(payload, signature, pubKey);
        Assert.True(isValid);
    }

    [Fact]
    public void VerifySignature_TamperedPayload_ReturnsFalse()
    {
        var (privKey, pubKey) = ProfileCrypto.GenerateKeyPair();
        var payload = "{\"generation\": 206, \"serverId\": \"us-01\"}";
        var tampered = "{\"generation\": 207, \"serverId\": \"us-01\"}";

        var signature = ProfileCrypto.SignData(payload, privKey);
        var isValid = ProfileCrypto.VerifySignature(tampered, signature, pubKey);

        Assert.False(isValid);
    }

    [Fact]
    public void VerifySignature_CorruptedSignature_ReturnsFalse()
    {
        var (_, pubKey) = ProfileCrypto.GenerateKeyPair();
        var payload = "{\"generation\": 206}";
        var badSig = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 });

        var isValid = ProfileCrypto.VerifySignature(payload, badSig, pubKey);
        Assert.False(isValid);
    }
}
