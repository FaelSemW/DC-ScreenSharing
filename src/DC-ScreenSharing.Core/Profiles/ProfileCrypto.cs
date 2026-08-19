using System.Security.Cryptography;
using System.Text;

namespace DCScreenSharing.Core.Profiles;

public static class ProfileCrypto
{
    // Default public key for verifying signed catalogs and profile manifests
    public const string DefaultPublicKeyXml = @"<RSAKeyValue><Modulus>w12P2/Jm0GkP9wJqYpA3g0lZgZpYV1oA/c9E7qL0yZlD8n5t9p2q1w==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        return (privateKeyPem, publicKeyPem);
    }

    public static string SignData(string payload, string privateKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var dataBytes = Encoding.UTF8.GetBytes(payload);
        var signatureBytes = rsa.SignData(dataBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signatureBytes);
    }

    public static bool VerifySignature(string payload, string signatureBase64, string publicKeyPem)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(signatureBase64) || string.IsNullOrWhiteSpace(publicKeyPem))
                return false;

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            var dataBytes = Encoding.UTF8.GetBytes(payload);
            var signatureBytes = Convert.FromBase64String(signatureBase64);
            return rsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            return false;
        }
    }
}
