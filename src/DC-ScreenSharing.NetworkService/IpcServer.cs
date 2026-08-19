using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DCScreenSharing.Networking;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.NetworkService;

public class IpcServer
{
    private readonly ProcessRoutingEngine _engine;
    private readonly CrashRecoveryManager _recovery;
    private readonly IAppLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _serverTask;

    private bool _isConnected;
    private string? _activeServerId;
    private string? _activeServerName;
    private DateTime? _connectedSinceUtc;
    private string? _lastError;

    public IpcServer(ProcessRoutingEngine engine, CrashRecoveryManager recovery, IAppLogger logger)
    {
        _engine = engine;
        _recovery = recovery;
        _logger = logger;
    }

    public void Start()
    {
        _serverTask = Task.Run(() => ListenLoopAsync(_cts.Token));
        _logger.Info($"IPC Server started listening on pipe: {Constants.PipeName}");
    }

    public void Stop()
    {
        _cts.Cancel();
        _engine.Stop();
        _recovery.ClearState();
        _logger.Info("IPC Server stopped.");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = CreateSecurePipeSecurity();
                var pipeServer = NamedPipeServerStreamAcl.Create(
                    Constants.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    8192,
                    8192,
                    pipeSecurity);

                await pipeServer.WaitForConnectionAsync(ct);
                _ = Task.Run(() => HandleClientAsync(pipeServer, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error accepting pipe connection: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }
    }

    private static PipeSecurity CreateSecurePipeSecurity()
    {
        var ps = new PipeSecurity();
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        ps.AddAccessRule(new PipeAccessRule(adminSid, PipeAccessRights.FullControl, AccessControlType.Allow));

        var authUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        ps.AddAccessRule(new PipeAccessRule(authUsersSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));

        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        ps.AddAccessRule(new PipeAccessRule(networkSid, PipeAccessRights.ReadWrite, AccessControlType.Deny));

        return ps;
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                var lengthBuffer = new byte[4];
                var readLength = await pipe.ReadAsync(lengthBuffer, ct);
                if (readLength < 4) return;

                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (messageLength is <= 0 or > 1024 * 1024) return;

                var messageBuffer = new byte[messageLength];
                var totalRead = 0;

                while (totalRead < messageLength)
                {
                    var chunk = await pipe.ReadAsync(messageBuffer.AsMemory(totalRead, messageLength - totalRead), ct);
                    if (chunk == 0) break;
                    totalRead += chunk;
                }

                var requestJson = Encoding.UTF8.GetString(messageBuffer, 0, totalRead);
                var request = JsonSerializer.Deserialize<IpcMessage>(requestJson);
                if (request == null) return;

                var response = ProcessRequest(request);

                var responseJson = JsonSerializer.Serialize(response);
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                var responseLengthPrefix = BitConverter.GetBytes(responseBytes.Length);

                await pipe.WriteAsync(responseLengthPrefix, ct);
                await pipe.WriteAsync(responseBytes, ct);
                await pipe.FlushAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error handling IPC client: {ex.Message}");
            }
        }
    }

    private IpcMessage ProcessRequest(IpcMessage request)
    {
        var response = new IpcMessage
        {
            Command = request.Command,
            RequestId = request.RequestId
        };

        switch (request.Command)
        {
            case IpcCommand.Ping:
                response.PayloadJson = "\"pong\"";
                break;

            case IpcCommand.GetStatus:
                var status = new ServiceStatusResponse
                {
                    IsRunning = true,
                    IsConnected = _isConnected && _engine.IsRunning,
                    ActiveServerId = _activeServerId,
                    ActiveServerName = _activeServerName,
                    ConnectedSinceUtc = _connectedSinceUtc,
                    ServiceVersion = "1.0.0",
                    LastError = _lastError
                };
                response.PayloadJson = JsonSerializer.Serialize(status);
                break;

            case IpcCommand.StartTunnel:
                var tunnelResp = HandleStartTunnel(request.PayloadJson);
                response.PayloadJson = JsonSerializer.Serialize(tunnelResp);
                break;

            case IpcCommand.StopTunnel:
                var stopResp = HandleStopTunnel();
                response.PayloadJson = JsonSerializer.Serialize(stopResp);
                break;

            case IpcCommand.GetDiagnostics:
                var diag = HandleGetDiagnostics();
                response.PayloadJson = JsonSerializer.Serialize(diag);
                break;

            case IpcCommand.CleanupOrphaned:
                _recovery.PerformStartupRecovery(_engine);
                response.PayloadJson = "\"cleaned\"";
                break;

            default:
                response.PayloadJson = "\"unknown_command\"";
                break;
        }

        return response;
    }

    private TunnelResponse HandleStartTunnel(string payloadJson)
    {
        try
        {
            _logger.Info("[IPC:StartTunnel] StartTunnel request received.");

            if (string.IsNullOrEmpty(payloadJson))
            {
                _logger.Warning("[IPC:StartTunnel] Missing tunnel configuration payload.");
                return new TunnelResponse { Success = false, ErrorCode = "MissingPayload", Message = "Missing tunnel configuration payload." };
            }

            var config = JsonSerializer.Deserialize<TunnelConfiguration>(payloadJson);
            if (config == null || string.IsNullOrEmpty(config.Endpoint) || string.IsNullOrEmpty(config.PrivateKey))
            {
                _logger.Warning("[IPC:StartTunnel] Invalid tunnel configuration payload.");
                return new TunnelResponse { Success = false, ErrorCode = "InvalidConfig", Message = "Invalid tunnel configuration." };
            }

            _logger.Info($"[IPC:StartTunnel] Request validated for server '{config.ServerId}' ({config.ServerName}). Initiating routing engine...");
            var (started, errorCode, errorMsg) = _engine.Start(config);

            if (started)
            {
                _isConnected = true;
                _activeServerId = config.ServerId;
                _activeServerName = config.ServerName;
                _connectedSinceUtc = DateTime.UtcNow;
                _lastError = null;

                _recovery.RecordState(true, config.ServerId, Constants.DefaultInterfaceName, null);

                _logger.Info("[IPC:StartTunnel] StartTunnel response: Success.");
                return new TunnelResponse
                {
                    Success = true,
                    Message = "Tunnel active.",
                    InterfaceName = Constants.DefaultInterfaceName
                };
            }
            else
            {
                _isConnected = false;
                _lastError = errorMsg;
                _recovery.ClearState();

                _logger.Error($"[IPC:StartTunnel] StartTunnel failed: [{errorCode}] {errorMsg}");
                return new TunnelResponse
                {
                    Success = false,
                    ErrorCode = errorCode,
                    Message = errorMsg
                };
            }
        }
        catch (Exception ex)
        {
            _logger.Error("[IPC:StartTunnel] Exception in HandleStartTunnel", ex);
            _isConnected = false;
            _lastError = ex.Message;
            return new TunnelResponse
            {
                Success = false,
                ErrorCode = "InternalServiceError",
                Message = $"Service internal error: {ex.Message}"
            };
        }
    }

    private TunnelResponse HandleStopTunnel()
    {
        try
        {
            _logger.Info("[IPC:StopTunnel] Stopping tunnel on IPC request...");
            _engine.Stop();
            _isConnected = false;
            _activeServerId = null;
            _activeServerName = null;
            _connectedSinceUtc = null;
            _recovery.ClearState();

            return new TunnelResponse { Success = true, Message = "Tunnel stopped successfully." };
        }
        catch (Exception ex)
        {
            _logger.Error("[IPC:StopTunnel] Error stopping tunnel", ex);
            return new TunnelResponse { Success = false, ErrorCode = "StopError", Message = ex.Message };
        }
    }

    private DiagnosticsData HandleGetDiagnostics()
    {
        return new DiagnosticsData
        {
            ServiceVersion = "1.0.0",
            OsVersion = Environment.OSVersion.VersionString,
            Is64Bit = Environment.Is64BitOperatingSystem,
            TunnelActive = _isConnected && _engine.IsRunning,
            ActiveServerId = _activeServerId,
            ServiceUptimeSeconds = 0,
            NetworkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Name).ToList(),
            SanitizedRecentLogs = _logger.GetRecentLogs(30).ToList()
        };
    }
}
