using System.Security.Cryptography;
using System.Text;

namespace DCScreenSharing.Core.Security;

public static class CredentialCrypto
{
    private const int NonceSize = 12; // 96 bits standard for AES-GCM
    private const int TagSize = 16;   // 128 bits authentication tag
    private const string EnvKeyName = "OPENVPN_CREDENTIALS_ENCRYPTION_KEY";
    private static readonly byte[] FallbackSalt = Encoding.UTF8.GetBytes("DCSS_OPENVPN_STORAGE_SALT_V1");

    public static byte[] GetMasterKey(string? explicitKey = null)
    {
        var keyStr = explicitKey;
        if (string.IsNullOrWhiteSpace(keyStr))
        {
            keyStr = Environment.GetEnvironmentVariable(EnvKeyName);
        }

        if (string.IsNullOrWhiteSpace(keyStr))
        {
            keyStr = "dcss-openvpn-default-storage-key-prod-change-me-32b";
        }

        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(keyStr));
    }

    public static string Encrypt(string plaintext, string? encryptionKey = null)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;

        var key = GetMasterKey(encryptionKey);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using (var aesGcm = new AesGcm(key, TagSize))
        {
            aesGcm.Encrypt(nonce, plainBytes, ciphertext, tag, FallbackSalt);
        }

        // Combine: nonce (12) + tag (16) + ciphertext (N)
        var result = new byte[NonceSize + TagSize + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, result, NonceSize, TagSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize + TagSize, ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    public static string Decrypt(string encryptedBase64, string? encryptionKey = null)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64)) return string.Empty;

        try
        {
            var raw = Convert.FromBase64String(encryptedBase64.Trim());
            if (raw.Length < NonceSize + TagSize) return string.Empty;

            var key = GetMasterKey(encryptionKey);

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var ciphertextLength = raw.Length - NonceSize - TagSize;
            var ciphertext = new byte[ciphertextLength];

            Buffer.BlockCopy(raw, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(raw, NonceSize, tag, 0, TagSize);
            Buffer.BlockCopy(raw, NonceSize + TagSize, ciphertext, 0, ciphertextLength);

            var plainBytes = new byte[ciphertextLength];

            using (var aesGcm = new AesGcm(key, TagSize))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plainBytes, FallbackSalt);
            }

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
