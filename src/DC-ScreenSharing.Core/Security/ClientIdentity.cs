using System.Security.Cryptography;
using System.Text;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Core.Security;

public class ClientIdentity
{
    private readonly string _storagePath;
    private readonly IAppLogger _logger;
    private readonly object _lock = new();

    public string ClientId { get; private set; } = string.Empty;
    public string PublicKeyPem { get; private set; } = string.Empty;
    public bool IsEnrolled => !string.IsNullOrEmpty(ClientId);
    private string _privateKeyPem = string.Empty;

    public ClientIdentity(string? storagePath = null, IAppLogger? logger = null)
    {
        _logger = logger ?? new FileLogger(Path.GetTempPath());
        _storagePath = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DC-ScreenSharing", "identity.dat");

        InitializeIdentity();
    }

    private void InitializeIdentity()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (File.Exists(_storagePath))
                {
                    var fileBytes = File.ReadAllBytes(_storagePath);
                    try
                    {
                        var decryptedBytes = ProtectedData.Unprotect(fileBytes, null, DataProtectionScope.CurrentUser);
                        var data = Encoding.UTF8.GetString(decryptedBytes).Split('|', 3);
                        if (data.Length == 3)
                        {
                            ClientId = data[0];
                            PublicKeyPem = data[1];
                            _privateKeyPem = data[2];
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Could not decrypt identity: {ex.Message}. Regenerating.");
                    }
                }

                // Generate new asymmetric key pair
                var (priv, pub) = ProfileCrypto.GenerateKeyPair();
                PublicKeyPem = pub;
                _privateKeyPem = priv;
                ClientId = string.Empty; // Not yet enrolled with ProfileService

                SaveIdentityInternal();
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to initialize client identity", ex);
            }
        }
    }

    public void SetEnrolledClientId(string clientId)
    {
        lock (_lock)
        {
            ClientId = clientId;
            SaveIdentityInternal();
        }
    }

    private void SaveIdentityInternal()
    {
        var rawData = $"{ClientId}|{PublicKeyPem}|{_privateKeyPem}";
        var rawBytes = Encoding.UTF8.GetBytes(rawData);
        var encrypted = ProtectedData.Protect(rawBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_storagePath, encrypted);
    }

    public string SignPayload(string payload)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_privateKeyPem))
                throw new InvalidOperationException("Client identity private key not loaded.");

            return ProfileCrypto.SignData(payload, _privateKeyPem);
        }
    }
}
