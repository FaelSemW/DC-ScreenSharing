using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Core.Transports;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking.Transports;

public class VpnTransportFactory
{
    private readonly WireGuardTransport _wireGuardTransport;
    private readonly OpenVpnTransport _openVpnTransport;
    private readonly IAppLogger _logger;

    public VpnTransportFactory(NetworkServiceClient client, IAppLogger logger)
    {
        _logger = logger;
        _wireGuardTransport = new WireGuardTransport(client, logger);
        _openVpnTransport = new OpenVpnTransport(client, logger);
    }

    public IVpnTransport GetTransport(string? protocol)
    {
        if (VpnProtocol.IsOpenVpn(protocol))
        {
            return _openVpnTransport;
        }

        // Default to WireGuard for maximum compatibility
        return _wireGuardTransport;
    }

    public IVpnTransport GetTransport(ServerEntry server)
    {
        return GetTransport(server.Protocol);
    }
}
