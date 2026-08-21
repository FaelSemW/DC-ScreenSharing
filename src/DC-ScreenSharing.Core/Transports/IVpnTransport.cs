using DCScreenSharing.Core.Profiles;
using DCScreenSharing.Shared.Contracts;

namespace DCScreenSharing.Core.Transports;

public interface IVpnTransport
{
    string ProtocolName { get; }
    bool IsSupported { get; }
    Task<TunnelResponse> ConnectAsync(ServerEntry server, ServerProfile profile, CancellationToken cancellationToken = default);
    Task<TunnelResponse> DisconnectAsync(CancellationToken cancellationToken = default);
    Task<ServiceStatusResponse?> GetStatusAsync(CancellationToken cancellationToken = default);
}
