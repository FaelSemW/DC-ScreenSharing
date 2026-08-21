using System.Text.Json;
using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.Security;
using DCScreenSharing.Core.Transports;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking.Transports;

public class OpenVpnTransport : IVpnTransport
{
    private readonly NetworkServiceClient _client;
    private readonly IAppLogger _logger;

    public string ProtocolName => VpnProtocol.OpenVpn;
    public bool IsSupported => true;

    public OpenVpnTransport(NetworkServiceClient client, IAppLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<TunnelResponse> ConnectAsync(ServerEntry server, ServerProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Openvpn == null)
        {
            return new TunnelResponse
            {
                Success = false,
                ErrorCode = "InvalidOpenVpnProfile",
                Message = "OpenVPN configuration is missing from server profile."
            };
        }

        var ovpnConfig = profile.Openvpn;
        var primaryRemote = ovpnConfig.RemoteEndpoints.FirstOrDefault();
        var host = primaryRemote?.Host ?? "unknown";
        var port = primaryRemote?.Port ?? 1194;

        // Ensure password is decrypted for runtime if it was stored encrypted
        string? plaintextPassword = null;
        if (!string.IsNullOrEmpty(ovpnConfig.EncryptedPassword))
        {
            try
            {
                plaintextPassword = CredentialCrypto.Decrypt(ovpnConfig.EncryptedPassword);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not decrypt profile password directly: {ex.Message}");
            }
        }

        var runtimeConfig = new OpenVpnProfileConfig
        {
            Device = ovpnConfig.Device,
            Protocol = ovpnConfig.Protocol,
            RemoteEndpoints = ovpnConfig.RemoteEndpoints,
            Cipher = ovpnConfig.Cipher,
            Auth = ovpnConfig.Auth,
            ResolvRetry = ovpnConfig.ResolvRetry,
            Nobind = ovpnConfig.Nobind,
            PersistKey = ovpnConfig.PersistKey,
            PersistTun = ovpnConfig.PersistTun,
            CaCert = ovpnConfig.CaCert,
            ClientCert = ovpnConfig.ClientCert,
            ClientKey = ovpnConfig.ClientKey,
            TlsAuthKey = ovpnConfig.TlsAuthKey,
            TlsCryptKey = ovpnConfig.TlsCryptKey,
            TlsCryptV2Key = ovpnConfig.TlsCryptV2Key,
            KeyDirection = ovpnConfig.KeyDirection,
            Username = ovpnConfig.Username,
            EncryptedPassword = plaintextPassword ?? ovpnConfig.EncryptedPassword,
            CredentialSetId = ovpnConfig.CredentialSetId
        };

        var config = new TunnelConfiguration
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Protocol = VpnProtocol.OpenVpn,
            Endpoint = host,
            Port = port,
            OpenVpnProfileJson = JsonSerializer.Serialize(runtimeConfig, new JsonSerializerOptions { WriteIndented = false })
        };

        _logger.Info($"[OpenVpnTransport] Connecting to {server.Name} via OpenVPN ({host}:{port})...");
        return await _client.StartTunnelAsync(config, ct: cancellationToken);
    }

    public async Task<TunnelResponse> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("[OpenVpnTransport] Disconnecting OpenVPN tunnel...");
        return await _client.StopTunnelAsync(ct: cancellationToken);
    }

    public async Task<ServiceStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return await _client.GetStatusAsync(ct: cancellationToken);
    }
}
