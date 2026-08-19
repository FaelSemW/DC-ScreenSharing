using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;

namespace DCScreenSharing.Networking;

public class NetworkServiceClient
{
    private readonly string _pipeName;
    private readonly IAppLogger _logger;

    public NetworkServiceClient(IAppLogger logger, string? pipeName = null)
    {
        _logger = logger;
        _pipeName = pipeName ?? Constants.PipeName;
    }

    public async Task<bool> PingAsync(int timeoutMs = 2000, CancellationToken ct = default)
    {
        try
        {
            var msg = new IpcMessage { Command = IpcCommand.Ping };
            var response = await SendMessageAsync(msg, timeoutMs, ct);
            return response != null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ServiceStatusResponse?> GetStatusAsync(int timeoutMs = 3000, CancellationToken ct = default)
    {
        try
        {
            var msg = new IpcMessage { Command = IpcCommand.GetStatus };
            var response = await SendMessageAsync(msg, timeoutMs, ct);
            if (!string.IsNullOrEmpty(response?.PayloadJson))
            {
                return JsonSerializer.Deserialize<ServiceStatusResponse>(response.PayloadJson);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"GetStatus IPC query failed: {ex.Message}");
        }

        return null;
    }

    public async Task<TunnelResponse> StartTunnelAsync(TunnelConfiguration config, int timeoutMs = 15000, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(config);
            var msg = new IpcMessage { Command = IpcCommand.StartTunnel, PayloadJson = payload };
            var response = await SendMessageAsync(msg, timeoutMs, ct);

            if (!string.IsNullOrEmpty(response?.PayloadJson))
            {
                var tunnelResp = JsonSerializer.Deserialize<TunnelResponse>(response.PayloadJson);
                if (tunnelResp != null)
                {
                    return tunnelResp;
                }

                return new TunnelResponse { Success = false, ErrorCode = "EmptyResponse", Message = "Empty response from network service." };
            }

            return new TunnelResponse { Success = false, ErrorCode = "NoResponse", Message = "No response received from network service." };
        }
        catch (OperationCanceledException)
        {
            _logger.Error("StartTunnel IPC call timed out while waiting for network service.");
            return new TunnelResponse
            {
                Success = false,
                ErrorCode = "ServiceTimeout",
                Message = "Network service timed out starting tunnel. Ensure DCSS.NetworkService is running."
            };
        }
        catch (Exception ex)
        {
            _logger.Error("Failed to invoke StartTunnel on NetworkService", ex);
            return new TunnelResponse
            {
                Success = false,
                ErrorCode = "IpcError",
                Message = $"Service communication failure: {ex.Message}"
            };
        }
    }

    public async Task<TunnelResponse> StopTunnelAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        try
        {
            var msg = new IpcMessage { Command = IpcCommand.StopTunnel };
            var response = await SendMessageAsync(msg, timeoutMs, ct);

            if (!string.IsNullOrEmpty(response?.PayloadJson))
            {
                return JsonSerializer.Deserialize<TunnelResponse>(response.PayloadJson) ??
                       new TunnelResponse { Success = true, Message = "Stopped." };
            }

            return new TunnelResponse { Success = true, Message = "Stop command dispatched." };
        }
        catch (Exception ex)
        {
            _logger.Warning("StopTunnel IPC call error", ex);
            return new TunnelResponse { Success = false, ErrorCode = "StopIpcError", Message = ex.Message };
        }
    }

    public async Task<DiagnosticsData?> GetDiagnosticsAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        try
        {
            var msg = new IpcMessage { Command = IpcCommand.GetDiagnostics };
            var response = await SendMessageAsync(msg, timeoutMs, ct);
            if (!string.IsNullOrEmpty(response?.PayloadJson))
            {
                return JsonSerializer.Deserialize<DiagnosticsData>(response.PayloadJson);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Failed to retrieve diagnostics via IPC", ex);
        }

        return null;
    }

    private async Task<IpcMessage?> SendMessageAsync(IpcMessage request, int timeoutMs, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeoutCts = new CancellationTokenSource(timeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await pipe.ConnectAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Named pipe '{_pipeName}' connection timed out after {timeoutMs}ms. Network service may not be running.");
        }

        var requestJson = JsonSerializer.Serialize(request);
        var requestBytes = Encoding.UTF8.GetBytes(requestJson);
        var lengthPrefix = BitConverter.GetBytes(requestBytes.Length);

        await pipe.WriteAsync(lengthPrefix, linkedCts.Token);
        await pipe.WriteAsync(requestBytes, linkedCts.Token);
        await pipe.FlushAsync(linkedCts.Token);

        var responseLengthBuffer = new byte[4];
        var readLength = await pipe.ReadAsync(responseLengthBuffer, linkedCts.Token);
        if (readLength < 4)
            return null;

        var responseLength = BitConverter.ToInt32(responseLengthBuffer, 0);
        var responseBuffer = new byte[responseLength];
        var totalRead = 0;

        while (totalRead < responseLength)
        {
            var chunk = await pipe.ReadAsync(responseBuffer.AsMemory(totalRead, responseLength - totalRead), linkedCts.Token);
            if (chunk == 0) break;
            totalRead += chunk;
        }

        var responseJson = Encoding.UTF8.GetString(responseBuffer, 0, totalRead);
        return JsonSerializer.Deserialize<IpcMessage>(responseJson);
    }
}
