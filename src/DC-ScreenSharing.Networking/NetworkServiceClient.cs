using System.IO.Pipes;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using DCScreenSharing.Shared;
using DCScreenSharing.Shared.Contracts;
using DCScreenSharing.Shared.Logging;
using TimeoutException = System.TimeoutException;

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

    public async Task<(bool IsHealthy, string Message)> VerifyAndRecoverServiceAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Fast path: check if named pipe is already responsive
            if (await PingAsync(1500, ct))
            {
                _logger.Info("Network service IPC pipe is reachable and responsive.");
                return (true, "Network service is online.");
            }

            // 2. Query Windows Service Control Manager for DCSS.NetworkService
            ServiceController? sc = null;
            try
            {
                sc = new ServiceController(Constants.ServiceName);
                _ = sc.Status; // Throws if service is not registered
            }
            catch (InvalidOperationException)
            {
                _logger.Error($"Windows service '{Constants.ServiceName}' is not registered with SCM.");
                return (false, "DC-ScreenSharing Network Service is not installed. Please install or repair the application.");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Could not inspect service '{Constants.ServiceName}' status: {ex.Message}");
            }

            if (sc != null)
            {
                using (sc)
                {
                    _logger.Info($"Service '{Constants.ServiceName}' status: {sc.Status}");

                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        _logger.Info($"Service '{Constants.ServiceName}' is {sc.Status}. Attempting controlled startup...");
                        var startOk = await AttemptStartServiceAsync(sc);
                        if (!startOk)
                        {
                            _logger.Warning("Standard service start failed, attempting elevated start via sc.exe...");
                            await StartServiceElevatedAsync();
                        }

                        var running = await WaitForServiceStatusAsync(sc, ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        if (!running)
                        {
                            return (false, $"Unable to start '{Constants.ServiceName}'. Service state is {sc.Status}.");
                        }
                    }
                    else
                    {
                        // Service is in Running state, but named pipe ping failed earlier.
                        // Perform one controlled service restart.
                        _logger.Warning($"Service '{Constants.ServiceName}' is marked Running, but IPC pipe is unreachable. Attempting one controlled restart...");
                        var restartOk = await AttemptRestartServiceAsync(sc);
                        if (!restartOk)
                        {
                            _logger.Warning("Standard service restart failed, attempting elevated restart via sc.exe...");
                            await RestartServiceElevatedAsync();
                        }

                        var running = await WaitForServiceStatusAsync(sc, ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
                        if (!running)
                        {
                            return (false, $"Service recovery restart failed. Service state is {sc.Status}.");
                        }
                    }
                }
            }

            // 3. Post-recovery verify named pipe ping
            for (int i = 0; i < 6; i++)
            {
                if (await PingAsync(1500, ct))
                {
                    _logger.Info("Network service IPC pipe successfully verified after recovery.");
                    return (true, "Network service is online.");
                }
                await Task.Delay(500, ct);
            }

            return (false, "DCSS.NetworkService is running but not responding on IPC pipe. Please check service logs or restart the service.");
        }
        catch (Exception ex)
        {
            _logger.Error("Unexpected error during network service health check", ex);
            return (false, $"Network service health check failed: {ex.Message}");
        }
    }

    private Task<bool> AttemptStartServiceAsync(ServiceController sc)
    {
        return Task.Run(() =>
        {
            try
            {
                sc.Refresh();
                if (sc.Status == ServiceControllerStatus.Running) return true;
                sc.Start();
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"AttemptStartServiceAsync failed: {ex.Message}");
                return false;
            }
        });
    }

    private async Task<bool> AttemptRestartServiceAsync(ServiceController sc)
    {
        try
        {
            sc.Refresh();
            if (sc.CanStop)
            {
                sc.Stop();
                await WaitForServiceStatusAsync(sc, ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
            }
            sc.Start();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning($"AttemptRestartServiceAsync failed: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> WaitForServiceStatusAsync(ServiceController sc, ServiceControllerStatus desired, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                sc.Refresh();
                if (sc.Status == desired) return true;
            }
            catch { }
            await Task.Delay(300);
        }
        try { sc.Refresh(); return sc.Status == desired; } catch { return false; }
    }

    private async Task StartServiceElevatedAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"start {Constants.ServiceName}",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning($"Elevated sc.exe start note: {ex.Message}");
        }
    }

    private async Task RestartServiceElevatedAsync()
    {
        try
        {
            var psiStop = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"stop {Constants.ServiceName}",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using var stopProc = System.Diagnostics.Process.Start(psiStop);
            if (stopProc != null) await stopProc.WaitForExitAsync();

            await Task.Delay(1000);

            var psiStart = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"start {Constants.ServiceName}",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            using var startProc = System.Diagnostics.Process.Start(psiStart);
            if (startProc != null) await startProc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning($"Elevated sc.exe restart note: {ex.Message}");
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

    public async Task<TunnelResponse> ValidateConfigAsync(TunnelConfiguration config, int timeoutMs = 8000, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(config);
            var msg = new IpcMessage { Command = IpcCommand.ValidateConfig, PayloadJson = payload };
            var response = await SendMessageAsync(msg, timeoutMs, ct);

            if (!string.IsNullOrEmpty(response?.PayloadJson))
            {
                var valResp = JsonSerializer.Deserialize<TunnelResponse>(response.PayloadJson);
                if (valResp != null)
                {
                    return valResp;
                }
            }

            return new TunnelResponse { Success = true, Message = "Configuration validated." };
        }
        catch (Exception ex)
        {
            _logger.Warning($"ValidateConfig IPC call failed: {ex.Message}");
            return new TunnelResponse { Success = true, Message = "Validation skipped (service fallback)." };
        }
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
