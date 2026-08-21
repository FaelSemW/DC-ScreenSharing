using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.Transports;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking.Transports;

public class WireGuardTransport : IVpnTransport
{
    private readonly NetworkServiceClient _client;
    private readonly IAppLogger _logger;

    public string ProtocolName => VpnProtocol.WireGuard;
    public bool IsSupported => true;

    public WireGuardTransport(NetworkServiceClient client, IAppLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<TunnelResponse> ConnectAsync(ServerEntry server, ServerProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Wireguard == null)
        {
            return new TunnelResponse
            {
                Success = false,
                ErrorCode = "InvalidWireGuardProfile",
                Message = "WireGuard configuration is missing from server profile."
            };
        }

        var config = new TunnelConfiguration
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Protocol = VpnProtocol.WireGuard,
            Endpoint = profile.Wireguard.Endpoint,
            Port = profile.Wireguard.Port,
            Address = profile.Wireguard.Address,
            Dns = profile.Wireguard.Dns,
            PrivateKey = profile.Wireguard.PrivateKey,
            PeerPublicKey = profile.Wireguard.PeerPublicKey,
            AllowedIps = profile.Wireguard.AllowedIps,
            Mtu = profile.Wireguard.Mtu,
            PersistentKeepalive = profile.Wireguard.PersistentKeepalive
        };

        _logger.Info($"[WireGuardTransport] Connecting to {server.Name} ({config.Endpoint}:{config.Port})...");
        return await _client.StartTunnelAsync(config, ct: cancellationToken);
    }

    public async Task<TunnelResponse> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("[WireGuardTransport] Disconnecting tunnel...");
        return await _client.StopTunnelAsync(ct: cancellationToken);
    }

    public async Task<ServiceStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        return await _client.GetStatusAsync(ct: cancellationToken);
    }
}
